MATCH (n:`Person`)
WHERE (NOT coalesce(n.`nickname` = $p0, (n.`nickname` IS NULL AND $p0 IS NULL)))
RETURN n.`id` AS `c0`, n.`born` AS `c1`, n.`name` AS `c2`, n.`nickname` AS `c3`, n.`version` AS `c4`
