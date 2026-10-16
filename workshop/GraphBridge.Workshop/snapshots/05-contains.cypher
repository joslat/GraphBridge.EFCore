MATCH (n:`Person`)
WHERE (coalesce(n.`name` IN $p0, false) OR (n.`name` IS NULL AND any(__gb_contains_value IN $p0 WHERE __gb_contains_value IS NULL)))
RETURN n.`name` AS `c2`
ORDER BY n.`name` IS NULL DESC, n.`name` ASC
