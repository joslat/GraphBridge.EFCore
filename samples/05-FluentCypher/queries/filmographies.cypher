MATCH (n0:`FGQPerson`)
WHERE n0.`RunId` = $p0
OPTIONAL MATCH (n0:`FGQPerson`)-[r2:`FGQ_ACTED`]->(n1:`FGQMovie`)
RETURN n0 {.`Id`, .`Name`, .`Nickname`, .`RunId`} AS c0, collect(DISTINCT n1 {.`Id`, .`RunId`, .`Title`, .`Year`})[..$p1] AS c1
ORDER BY c0.`Name` ASC
LIMIT $p2
