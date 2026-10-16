MATCH (n0:`FGQPerson`)-[r2:`FGQ_ACTED`]->(n1:`FGQMovie`)<-[r4:`FGQ_ACTED`]-(n3:`FGQPerson`)
WHERE n0.`RunId` = $p0 AND n0.`Name` = $p1 AND n0.`Id` <> n3.`Id`
WITH n3, count(DISTINCT n1) AS v5, count(*) AS v6
WHERE v5 >= $p2
RETURN n3.`Name` AS c0, v5 AS c1, v6 AS c2
ORDER BY c1 DESC, c0 ASC
LIMIT $p3
