MATCH (n0:`Person`)-[r2:`ACTED_IN`]->(n1:`Movie`)<-[r4:`ACTED_IN`]-(n3:`Person`)
WHERE n0.`name` = $p0 AND n3.`id` <> n0.`id`
RETURN DISTINCT n3.`name` AS c0
ORDER BY c0 ASC
LIMIT $p1
