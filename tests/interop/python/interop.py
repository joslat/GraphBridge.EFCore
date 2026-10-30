"""Neo4j's neo4j-graphrag on the same graph as GraphBridge (GraphRAG v2, GR-52).

The .NET interop tests (tests/GraphBridge.GraphRag.InteropTests) run this script with a JSON request on standard input
and read a JSON result from standard output. It connects with the GRAPHBRIDGE_NEO4J_* variables. No model is called:
both stacks embed with the same word-hashing function.

    embed     {"dimensions": 64, "texts": [...]}                          -> {"vectors": [[...], ...]}
    build     {"config": {...}, "dimensions": 64, "chunk_size": 200, "chunk_overlap": 0,
               "documents": [{"path": "...", "text": "..."}], "vector_index": "...", "fulltext_index": "..."}
              -> {"documents": n, "chunks": n}
    count     {"config": {...}}                                           -> {"documents", "chunks", "chunk_links", "next_links"}
    retrieve  {"vector_index": "...", "fulltext_index": "...", "query_text": "...", "query_vector": [...], "top_k": 3}
              -> {"vector": [{"properties": {...}, "score": s}], "hybrid": [...]}
"""

import asyncio
import json
import os
import sys

import neo4j
from neo4j_graphrag.components.embedder import TextChunkEmbedder
from neo4j_graphrag.components.kg_writer import Neo4jWriter
from neo4j_graphrag.components.lexical_graph import LexicalGraphBuilder
from neo4j_graphrag.components.text_splitters.fixed_size_splitter import FixedSizeSplitter
from neo4j_graphrag.components.types import DocumentInfo, LexicalGraphConfig
from neo4j_graphrag.embeddings.base import Embedder
from neo4j_graphrag.indexes import create_fulltext_index, create_vector_index
from neo4j_graphrag.retrievers import HybridRetriever, VectorCypherRetriever, VectorRetriever

# The same words as GraphBridge's sample HashingEmbedder (samples/GraphRagShared/SampleModels.cs): lower case, split on
# these characters, longer than two characters, not a stop word.
SEPARATORS = set(" \t\r\n.,;:!?()[]{}<>\"'`*#|/\\-_=+")
STOP_WORDS = {"the", "and", "for", "are", "with", "that", "this", "what", "how", "does", "which", "from", "into", "its", "can"}


def words(text):
    current, result = [], []
    for c in text.lower():
        if c in SEPARATORS:
            if current:
                result.append("".join(current))
                current = []
        else:
            current.append(c)
    if current:
        result.append("".join(current))
    return [w for w in result if len(w) > 2 and w not in STOP_WORDS]


def fnv1a(word):
    h = 2166136261
    for b in word.encode("utf-8"):
        h = ((h ^ b) * 16777619) & 0xFFFFFFFF
    return h


def embed(text, dimensions):
    vector = [0.0] * dimensions
    vector[0] = 1.0  # never all zeros, as in .NET
    for word in words(text):
        vector[1 + fnv1a(word) % (dimensions - 1)] += 1.0
    return vector


class HashingEmbedder(Embedder):
    def __init__(self, dimensions):
        super().__init__()
        self.dimensions = dimensions

    def embed_query(self, text):
        return embed(text, self.dimensions)

    async def async_embed_query(self, text):
        return embed(text, self.dimensions)


class NativeMergeWriter(Neo4jWriter):
    """neo4j-graphrag's writer with its one APOC call, apoc.merge.relationship, replaced by MERGE with a dynamic type.

    Neo4jWriter 1.22.0 writes relationships with APOC. The disposable database has no APOC (the Neo4j image ships it in
    labs, not enabled), so the writer would write the nodes and report FAILURE for the relationships. Everything else is the
    library's own: the nodes, their labels and properties, and the clean-up of the temporary IDs.
    """

    def _upsert_relationships(self, rels):
        rows = self._relationships_to_rows(rels)
        self.driver.execute_query(
            "CYPHER 25 UNWIND $rows AS row "
            "MATCH (start:__KGBuilder__ {__tmp_internal_id: row.start_node_id}), (end:__KGBuilder__ {__tmp_internal_id: row.end_node_id}) "
            "MERGE (start)-[rel:$(row.type)]->(end) SET rel += row.properties",
            parameters_={"rows": rows}, database_=self.neo4j_database)


def driver():
    uri = os.environ["GRAPHBRIDGE_NEO4J_URI"]
    auth = (os.environ["GRAPHBRIDGE_NEO4J_USERNAME"], os.environ["GRAPHBRIDGE_NEO4J_PASSWORD"])
    return neo4j.GraphDatabase.driver(uri, auth=auth)


def database():
    return os.environ.get("GRAPHBRIDGE_NEO4J_DATABASE") or None


async def build(request):
    config = LexicalGraphConfig(**request["config"])
    splitter = FixedSizeSplitter(chunk_size=request["chunk_size"], chunk_overlap=request["chunk_overlap"], approximate=False)
    embedder = TextChunkEmbedder(embedder=HashingEmbedder(request["dimensions"]))
    builder = LexicalGraphBuilder(config=config)
    chunks = 0
    with driver() as d:
        writer = NativeMergeWriter(driver=d, neo4j_database=database())
        for document in request["documents"]:
            text_chunks = await embedder.run(await splitter.run(document["text"]))
            graph = await builder.run(text_chunks=text_chunks, document_info=DocumentInfo(path=document["path"]))
            written = await writer.run(graph=graph.graph, lexical_graph_config=config)
            if written.status != "SUCCESS":
                raise SystemExit(f"The writer reported {written.status}: {written.metadata}")
            chunks += len(text_chunks.chunks)
        create_vector_index(d, request["vector_index"], config.chunk_node_label, config.chunk_embedding_property,
                            request["dimensions"], "cosine", neo4j_database=database())
        create_fulltext_index(d, request["fulltext_index"], config.chunk_node_label, [config.chunk_text_property],
                              neo4j_database=database())
        d.execute_query("CALL db.awaitIndexes(60)", database_=database())
    return {"documents": len(request["documents"]), "chunks": chunks}


def count(request):
    config = LexicalGraphConfig(**request["config"])
    queries = {
        "documents": f"MATCH (d:`{config.document_node_label}`) RETURN count(d) AS n",
        "chunks": f"MATCH (c:`{config.chunk_node_label}`) RETURN count(c) AS n",
        "chunk_links": f"MATCH (:`{config.chunk_node_label}`)-[r:`{config.chunk_to_document_relationship_type}`]->(:`{config.document_node_label}`) RETURN count(r) AS n",
        "next_links": f"MATCH (:`{config.chunk_node_label}`)-[r:`{config.next_chunk_relationship_type}`]->(:`{config.chunk_node_label}`) RETURN count(r) AS n",
    }
    with driver() as d:
        return {name: d.execute_query(query, database_=database()).records[0]["n"] for name, query in queries.items()}


def items(result):
    return [{"properties": {k: v for k, v in dict(record["node"]).items() if k != "embedding"}, "score": record["score"]}
            for record in result.records]


def retrieve(request):
    with driver() as d:
        vector = VectorRetriever(d, request["vector_index"], neo4j_database=database())
        hybrid = HybridRetriever(d, request["vector_index"], request["fulltext_index"], neo4j_database=database())
        return {
            "vector": items(vector.get_search_results(query_vector=request["query_vector"], top_k=request["top_k"])),
            "hybrid": items(hybrid.get_search_results(query_text=request["query_text"], query_vector=request["query_vector"],
                                                     top_k=request["top_k"])),
        }


# GR-16: the things GraphBridge extracts, read the way neo4j-graphrag reads its own: by __Entity__, FROM_CHUNK and name.
ENTITY_CONTEXT = (
    "WITH node, score OPTIONAL MATCH (node)<-[:FROM_CHUNK]-(entity:__Entity__) "
    "RETURN node.id AS id, score, collect(DISTINCT entity.name) AS entities"
)


def entities(request):
    chunk = request["chunk_node_label"]
    with driver() as d:
        counts = d.execute_query(
            f"MATCH (e:__Entity__:__KGBuilder__)-[m:FROM_CHUNK]->(:`{chunk}`) RETURN count(m) AS mentions, count(DISTINCT e) AS things",
            database_=database()).records[0]
        retriever = VectorCypherRetriever(d, request["vector_index"], ENTITY_CONTEXT, neo4j_database=database())
        found = retriever.get_search_results(query_vector=request["query_vector"], top_k=request["top_k"])
        return {
            "mentions": counts["mentions"],
            "things": counts["things"],
            "retrieved": [{"id": r["id"], "entities": sorted(r["entities"])} for r in found.records],
        }


def main():
    command = sys.argv[1]
    request = json.load(sys.stdin)
    if command == "embed":
        result = {"vectors": [embed(t, request["dimensions"]) for t in request["texts"]]}
    elif command == "build":
        result = asyncio.run(build(request))
    elif command == "count":
        result = count(request)
    elif command == "retrieve":
        result = retrieve(request)
    elif command == "entities":
        result = entities(request)
    else:
        raise SystemExit(f"Unknown command {command}")
    json.dump(result, sys.stdout, default=str)


if __name__ == "__main__":
    main()
