MATCH (n:`Movie`)
CALL (n) {
  OPTIONAL MATCH (parent)-[candidate:`DIRECTED`]->(n)
  WITH parent LIMIT 2
  RETURN collect(parent) AS parents
}
WITH n, size(parents) AS `__gb_node_reference_count`, CASE WHEN size(parents) = 1 THEN parents[0] ELSE null END AS parent
WITH n, `__gb_node_reference_count`, coalesce(parent:`Person`, false) AS `__gb_node_reference_principal`, CASE WHEN parent:`Person` THEN parent ELSE null END AS s
WHERE coalesce(n.`released` >= $p0, false)
RETURN n.`id` AS `c0`, s.`id` AS `c1`, n.`released` AS `c2`, n.`tagline` AS `c3`, n.`title` AS `c4`, `__gb_node_reference_count` AS `__gb_node_reference_count`, `__gb_node_reference_principal` AS `__gb_node_reference_principal`
ORDER BY n.`released` IS NULL DESC, n.`released` ASC, n.`title` IS NULL DESC, n.`title` ASC
