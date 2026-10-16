MATCH (n0:`FGQPerson`)
WHERE n0.`RunId` = $p0 AND EXISTS { MATCH (n0:`FGQPerson`)-[r2:`FGQ_FRIEND`]->(n1:`FGQPerson`)
WHERE EXISTS { MATCH (n1:`FGQPerson`)-[r4:`FGQ_FRIEND`]->(n3:`FGQPerson`)
WHERE n3.`Name` = $p1 } }
RETURN n0.`Name` AS c0
ORDER BY c0 ASC
LIMIT $p2
