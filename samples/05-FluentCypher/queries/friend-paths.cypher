MATCH path3=(n0:`FGQPerson`)-[r2:`FGQ_FRIEND`*1..3]->(n1:`FGQPerson`)
WHERE n0.`RunId` = $p0 AND n0.`Name` = $p1
RETURN path3 AS c0
LIMIT $p2
