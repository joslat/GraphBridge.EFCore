MATCH (n0:`Person`)-[r2:`ACTED_IN`]->(n1:`Movie`)<-[r4:`ACTED_IN`]-(n3:`Person`)
WHERE n0.`name` IN $p0 AND NOT (n3.`name` IN $p1) AND NOT (n3.`name` STARTS WITH $p2)
RETURN DISTINCT n3.`name` AS c0
ORDER BY c0 ASC
LIMIT $p3
