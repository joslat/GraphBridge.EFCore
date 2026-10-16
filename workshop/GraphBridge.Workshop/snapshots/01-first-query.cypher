MATCH (n:`Person`)
WHERE coalesce(n.`name` = $p0, (n.`name` IS NULL AND $p0 IS NULL))
RETURN n.`id` AS `c0`, n.`born` AS `c1`, n.`name` AS `c2`, n.`nickname` AS `c3`, n.`version` AS `c4`
ORDER BY n.`name` IS NULL DESC, n.`name` ASC
LIMIT $p1
