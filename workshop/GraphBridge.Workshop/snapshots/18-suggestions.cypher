MATCH (n0:`Person`)-[r2:`FOLLOWS`]->(n1:`Person`)-[r4:`FOLLOWS`]->(n3:`Person`)
WHERE n0.`name` = $p0 AND n3.`id` <> n0.`id` AND NOT EXISTS { MATCH (n0:`Person`)-[r5:`FOLLOWS`]->(n3:`Person`) }
RETURN DISTINCT n3.`name` AS c0
ORDER BY c0 ASC
LIMIT $p1
