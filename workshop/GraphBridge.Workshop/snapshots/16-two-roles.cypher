MATCH (n0:`Person`)-[r2:`ACTED_IN`]->(n1:`Movie`)<-[r3:`ACTED_IN`]-(n0:`Person`)
RETURN DISTINCT n0.`name` AS c0
ORDER BY c0 ASC
LIMIT $p0
