MATCH (n0:`FGQPerson`)-[r2:`FGQ_ACTED`]->(n1:`FGQMovie`)
WHERE n0.`RunId` = $p0 AND n0.`Name` IN $p1 AND n1.`Title` STARTS WITH $p2
RETURN DISTINCT n0.`Name` AS c0
ORDER BY c0 ASC
LIMIT $p3
