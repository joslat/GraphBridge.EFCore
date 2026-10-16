MATCH (n0:`FGQPerson`)
WHERE n0.`RunId` = $p0
OPTIONAL MATCH (n0:`FGQPerson`)-[r2:`FGQ_ACTED`]->(n1:`FGQMovie`)
WHERE n1.`Year` > $p1
RETURN n0.`Name` AS c0, n1.`Title` AS c1, n1.`Year` AS c2
ORDER BY c0 ASC
LIMIT $p2
