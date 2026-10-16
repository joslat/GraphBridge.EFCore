MATCH (n:`Person`)
WHERE coalesce(n.`born` < $p0, false)
RETURN n.`name` AS `c2`
ORDER BY n.`born` IS NULL DESC, n.`born` ASC
