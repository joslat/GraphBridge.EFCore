MATCH (n:`Review`)
CALL (n) {
  OPTIONAL MATCH (parent)-[candidate:`HAS_REVIEW`]->(n)
  WITH parent LIMIT 2
  RETURN collect(parent) AS parents
}
WITH n, size(parents) AS `__gb_node_reference_count`, CASE WHEN size(parents) = 1 THEN parents[0] ELSE null END AS parent
WITH n, `__gb_node_reference_count`, coalesce(parent:`Movie`, false) AS `__gb_node_reference_principal`, CASE WHEN parent:`Movie` THEN parent ELSE null END AS s
WHERE coalesce(CASE WHEN n.`createdAt` IS NULL THEN null ELSE datetime({datetime: n.`createdAt`, timezone: '+00:00'}) END >= CASE WHEN $p0 IS NULL THEN null ELSE datetime({datetime: $p0, timezone: '+00:00'}) END, false)
RETURN n.`id` AS `c0`, s.`id` AS `c2`, n.`createdAt` AS `c1`, `__gb_node_reference_count` AS `__gb_node_reference_count`, `__gb_node_reference_principal` AS `__gb_node_reference_principal`
ORDER BY CASE WHEN n.`createdAt` IS NULL THEN null ELSE datetime({datetime: n.`createdAt`, timezone: '+00:00'}) END IS NULL DESC, CASE WHEN n.`createdAt` IS NULL THEN null ELSE datetime({datetime: n.`createdAt`, timezone: '+00:00'}) END ASC
