// Generated proposal for declared 2026.09.0 community.
// Review before explicit deployment. No live capabilities or graph data were observed.
CREATE CONSTRAINT `gb_ct_9087c3083fdb96d566bdb147` FOR (n:`OrganizationSamplePerson`) REQUIRE (n.`id`) IS UNIQUE;
CREATE CONSTRAINT `gb_ct_9db10fc32a87e783c9a597a1` FOR ()-[r:`ORGANIZATION_SAMPLE_WORKS_FOR`]-() REQUIRE (r.`employmentId`) IS UNIQUE;
CREATE CONSTRAINT `gb_ct_edfa01b65b64ce7a4725cf8b` FOR (n:`OrganizationSampleOrganization`) REQUIRE (n.`id`) IS UNIQUE;
