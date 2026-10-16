MATCH (n0:`Person`)
WHERE n0.`nickname` <> $p0
RETURN n0.`name` AS c0
ORDER BY c0 ASC
LIMIT $p1
