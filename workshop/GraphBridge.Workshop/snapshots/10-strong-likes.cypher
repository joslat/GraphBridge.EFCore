MATCH (s:`Person`)-[r:`LIKES`]->(t:`Movie`)
CALL (s, t) {
  MATCH (s)-[candidate:`LIKES`]->(t)
  WITH candidate LIMIT 2
  RETURN count(*) AS `__gb_association_count`
}
WITH s, r, t, `__gb_association_count`
WHERE coalesce(r.`stars` >= $p0, false)
RETURN s.`id` AS `c0`, t.`id` AS `c1`, r.`stars` AS `c2`, `__gb_association_count` AS `__gb_association_count`
ORDER BY r.`stars` IS NULL ASC, r.`stars` DESC
