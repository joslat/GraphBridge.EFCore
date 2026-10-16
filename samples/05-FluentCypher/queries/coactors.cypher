MATCH (n0:`FGQPerson`)-[r2:`FGQ_ACTED`]->(n1:`FGQMovie`)<-[r4:`FGQ_ACTED`]-(n3:`FGQPerson`)
WHERE n0.`RunId` = $p0 AND n0.`Name` = $p1 AND n3.`Id` <> n0.`Id`
RETURN DISTINCT n3.`Name` AS c0
ORDER BY c0 ASC
LIMIT $p2
