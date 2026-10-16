MATCH path3=(n0:`Person`)-[r2:`FOLLOWS`*1..5]->(n1:`Person`)
WHERE n0.`name` = $p0
RETURN path3 AS c0
LIMIT $p1
