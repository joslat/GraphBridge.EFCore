MATCH (n:`Person`)
WHERE coalesce(n.`id` = $p0, (n.`id` IS NULL AND $p0 IS NULL))
RETURN n.`id` AS `c0`, n.`born` AS `c1`, n.`name` AS `c2`, n.`nickname` AS `c3`, n.`version` AS `c4`
