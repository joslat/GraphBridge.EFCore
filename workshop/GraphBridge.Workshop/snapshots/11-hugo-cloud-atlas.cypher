MATCH (s:`Person`)-[r:`ACTED_IN`]->(t:`Movie`)
CALL (r) {
  MATCH ()-[candidate:`ACTED_IN`]->()
  WHERE candidate.`id` = r.`id`
  WITH candidate LIMIT 2
  RETURN count(*) AS `__gb_identified_edge_count`
}
WITH s, r, t, `__gb_identified_edge_count`
WHERE (coalesce(s.`id` = $p0, (s.`id` IS NULL AND $p0 IS NULL)) AND coalesce(t.`id` = $p1, (t.`id` IS NULL AND $p1 IS NULL)))
RETURN r.`id` AS `c0`, t.`id` AS `c2`, s.`id` AS `c3`, r.`billing` AS `c1`, r.`role` AS `c4`, `__gb_identified_edge_count` AS `__gb_identified_edge_count`
ORDER BY r.`billing` IS NULL DESC, r.`billing` ASC
