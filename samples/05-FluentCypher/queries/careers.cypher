MATCH (n0:`FGQPerson`)
WHERE n0.`RunId` = $p0
OPTIONAL MATCH (n0:`FGQPerson`)-[r2:`FGQ_ACTED`]->(n1:`FGQMovie`)
RETURN n0.`Name` AS c0, min(n1.`Year`) AS c1, max(n1.`Year`) AS c2, collect(DISTINCT n1.`Title`)[..$p1] AS c3
ORDER BY c0 ASC
LIMIT $p2
