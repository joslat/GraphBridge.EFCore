MATCH (n:`Movie`)
CALL (n) {
  OPTIONAL MATCH (parent)-[candidate:`DIRECTED`]->(n)
  WITH parent LIMIT 2
  RETURN collect(parent) AS parents
}
WITH n, size(parents) AS `__gb_node_reference_count`, CASE WHEN size(parents) = 1 THEN parents[0] ELSE null END AS parent
WITH n, `__gb_node_reference_count`, coalesce(parent:`Person`, false) AS `__gb_node_reference_principal`, CASE WHEN parent:`Person` THEN parent ELSE null END AS s
WHERE coalesce(n.`title` = $p0, (n.`title` IS NULL AND $p0 IS NULL))
WITH n, s, `__gb_node_reference_count`, `__gb_node_reference_principal`
CALL (n) {
  MATCH (n)-[:`HAS_REVIEW`]->(i)
CALL (i) {
  OPTIONAL MATCH (parent)-[candidate:`HAS_REVIEW`]->(i)
  WITH parent LIMIT 2
  RETURN collect(parent) AS parents
}
WITH i, size(parents) AS `__gb_node_reference_count`, CASE WHEN size(parents) = 1 THEN parents[0] ELSE null END AS parent
WITH i, `__gb_node_reference_count`, coalesce(parent:`Movie`, false) AS `__gb_node_reference_principal`, CASE WHEN parent:`Movie` THEN parent ELSE null END AS ip
  RETURN collect({`c0`: i.`id`, `c2`: ip.`id`, `c1`: i.`createdAt`, `c3`: i.`rating`, `c4`: i.`text`, `__gb_include_label`: coalesce(i:`Review`, false), `__gb_node_reference_count`: `__gb_node_reference_count`, `__gb_node_reference_principal`: `__gb_node_reference_principal`}) AS `__gb_include_rows`
}
RETURN n.`id` AS `c0`, s.`id` AS `c1`, n.`released` AS `c2`, n.`tagline` AS `c3`, n.`title` AS `c4`, `__gb_node_reference_count` AS `__gb_node_reference_count`, `__gb_node_reference_principal` AS `__gb_node_reference_principal`, `__gb_include_rows` AS `__gb_include_rows`
