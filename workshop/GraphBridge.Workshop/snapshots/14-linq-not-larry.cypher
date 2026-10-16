MATCH (n:`Person`)
WHERE (NOT coalesce(n.`nickname` = $p0, (n.`nickname` IS NULL AND $p0 IS NULL)))
RETURN n.`name` AS `c2`
ORDER BY n.`name` IS NULL DESC, n.`name` ASC
