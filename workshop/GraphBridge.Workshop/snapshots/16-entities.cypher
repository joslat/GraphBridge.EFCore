MATCH (n0:`Person`)-[r2:`ACTED_IN`]->(n1:`Movie`)
WHERE n0.`name` = $p0
RETURN DISTINCT n1 {.`id`, .`released`, .`tagline`, .`title`, __gb_reference: COLLECT { MATCH (__gb_parent)-[:`DIRECTED`]->(n1) RETURN __gb_parent {.`id`, __gb_principal: (__gb_parent:`Person`)} LIMIT 2 }} AS c0
ORDER BY c0.`released` ASC, c0.`title` ASC
LIMIT $p1
