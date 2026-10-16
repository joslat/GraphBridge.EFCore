# GraphBridge generation report

Source SHA-256: `503ad72011d22ca230b289e7b240607d594aa68e269c5a9b9a768282a97c3fde`.

Finalized EF storage fingerprint: `dca59d8bdee92c2d02c0e6ed57b9901f4de2c764c5271ca8bf1ad1bd8a5e8a3e`.

Declared target: 2026.09.0 community. This is an offline declaration, not live capability detection.

Generated model: 2 node types and 1 identified relationship types.

The compiled generated DbContext and its finalized EF IModel are the source of the storage manifest. Source provenance is separate from the storage fingerprint. Application partial customizations are not compiled by this tool; they can change the application's runtime model and fingerprint.

RDFS domain/range describe inference, not closed-world requiredness or uniqueness. The explicit persistence policy supplies keys, property types, conversions, direction and deletion. No OWL reasoning or imports run. Identified relationship payloads and parallel identities are persistence extensions to RDF triple-set semantics. SHACL validation is separate and does not become runtime EF enforcement.

Date-only values, when requested, use the policy's explicit invariant ISO string conversion; no native Neo4j temporal mapping is implied. Enterprise proposals require that declared edition; Graph Type replacement output is not generated. Schema text is a reviewable proposal and generation performs no deployment or data migration.

## Diagnostics

| Severity | Code | Subject | Message |
|---|---|---|---|
| Info | PolicyPropertyEnrichment | https://example.org/org#Organization/Id | This property has no ontology IRI; it is explicitly supplied by persistence policy. |
| Info | PolicyPropertyEnrichment | https://example.org/org#Person/Id | This property has no ontology IRI; it is explicitly supplied by persistence policy. |
| Info | IdentifiedEdgeReification | https://example.org/org#worksFor | Application edge IDs and payload are explicit policy enrichment. Parallel identified episodes do not preserve the set identity of a single RDF object-property triple. |
| Info | PolicyPropertyEnrichment | https://example.org/org#worksFor/EmploymentId | This property has no ontology IRI; it is explicitly supplied by persistence policy. |
| Info | PolicyPropertyEnrichment | https://example.org/org#worksFor/OrganizationId | This property has no ontology IRI; it is explicitly supplied by persistence policy. |
| Info | PolicyPropertyEnrichment | https://example.org/org#worksFor/PersonId | This property has no ontology IRI; it is explicitly supplied by persistence policy. |
| Info | PolicyPropertyEnrichment | https://example.org/org#worksFor/Role | This property has no ontology IRI; it is explicitly supplied by persistence policy. |
| Info | CalendarDateConversion | https://example.org/org#worksFor/ValidFrom | DateOnly stores an invariant yyyy-MM-dd calendar date. Time zones, instants and arbitrary xsd:date lexical forms are not preserved. |
| Info | PolicyPropertyEnrichment | https://example.org/org#worksFor/ValidFrom | This property has no ontology IRI; it is explicitly supplied by persistence policy. |
| Info | CalendarDateConversion | https://example.org/org#worksFor/ValidTo | DateOnly stores an invariant yyyy-MM-dd calendar date. Time zones, instants and arbitrary xsd:date lexical forms are not preserved. |
| Info | PolicyPropertyEnrichment | https://example.org/org#worksFor/ValidTo | This property has no ontology IRI; it is explicitly supplied by persistence policy. |
| Info | SemanticBoundary | ontology | Only explicitly declared classes and properties are inspected. No OWL/RDFS closure is computed; domain/range are inference-bearing statements, not requiredness or closed-world constraints. |
| Info | ShaclSeparateValidation | shapes | SHACL is retained for separate RDF data validation; no shape constraint is silently promoted to EF or database enforcement. |

## Storage caveats

- relationship:ORGANIZATION_SAMPLE_WORKS_FOR.validFrom: opaque conversion; equal fingerprints describe storage shape, not converter semantic equivalence.
- relationship:ORGANIZATION_SAMPLE_WORKS_FOR.validTo: opaque conversion; equal fingerprints describe storage shape, not converter semantic equivalence.
- relationship:ORGANIZATION_SAMPLE_WORKS_FOR: indexes containing endpoint slots are excluded; endpoint FK values are not stored relationship properties.

## Empty-target comparison

Missing constraints are expected before explicit deployment.

- GraphSchemaIssue { Status = Fail, Category = Identity, Code = MissingIdentityConstraint, Subject = NODE:OrganizationSampleOrganization(id), Message = No exact node/relationship property uniqueness or key constraint covers these store properties. }
- GraphSchemaIssue { Status = Warn, Category = Existence, Code = PropertyExistenceApplicationEnforced, Subject = NODE:OrganizationSampleOrganization(id), Message = Required property existence is checked by the provider but is not enforced by the observed database schema. }
- GraphSchemaIssue { Status = Warn, Category = PropertyType, Code = PropertyTypeApplicationEnforced, Subject = NODE:OrganizationSampleOrganization(id), Message = Provider expects STRING; no property type constraint was observed. }
- GraphSchemaIssue { Status = Warn, Category = Capability, Code = ProposalCapabilityUnavailable, Subject = NODE:OrganizationSampleOrganization(id), Message = ExistenceConstraint support is Unsupported; no DDL is proposed. }
- GraphSchemaIssue { Status = Warn, Category = Capability, Code = ProposalCapabilityUnavailable, Subject = NODE:OrganizationSampleOrganization(id), Message = PropertyTypeConstraint support is Unsupported; no DDL is proposed. }
- GraphSchemaIssue { Status = Warn, Category = Existence, Code = PropertyExistenceApplicationEnforced, Subject = NODE:OrganizationSampleOrganization(name), Message = Required property existence is checked by the provider but is not enforced by the observed database schema. }
- GraphSchemaIssue { Status = Warn, Category = PropertyType, Code = PropertyTypeApplicationEnforced, Subject = NODE:OrganizationSampleOrganization(name), Message = Provider expects STRING; no property type constraint was observed. }
- GraphSchemaIssue { Status = Warn, Category = Capability, Code = ProposalCapabilityUnavailable, Subject = NODE:OrganizationSampleOrganization(name), Message = ExistenceConstraint support is Unsupported; no DDL is proposed. }
- GraphSchemaIssue { Status = Warn, Category = Capability, Code = ProposalCapabilityUnavailable, Subject = NODE:OrganizationSampleOrganization(name), Message = PropertyTypeConstraint support is Unsupported; no DDL is proposed. }
- GraphSchemaIssue { Status = Fail, Category = Identity, Code = MissingIdentityConstraint, Subject = NODE:OrganizationSamplePerson(id), Message = No exact node/relationship property uniqueness or key constraint covers these store properties. }
- GraphSchemaIssue { Status = Warn, Category = Existence, Code = PropertyExistenceApplicationEnforced, Subject = NODE:OrganizationSamplePerson(id), Message = Required property existence is checked by the provider but is not enforced by the observed database schema. }
- GraphSchemaIssue { Status = Warn, Category = PropertyType, Code = PropertyTypeApplicationEnforced, Subject = NODE:OrganizationSamplePerson(id), Message = Provider expects STRING; no property type constraint was observed. }
- GraphSchemaIssue { Status = Warn, Category = Capability, Code = ProposalCapabilityUnavailable, Subject = NODE:OrganizationSamplePerson(id), Message = ExistenceConstraint support is Unsupported; no DDL is proposed. }
- GraphSchemaIssue { Status = Warn, Category = Capability, Code = ProposalCapabilityUnavailable, Subject = NODE:OrganizationSamplePerson(id), Message = PropertyTypeConstraint support is Unsupported; no DDL is proposed. }
- GraphSchemaIssue { Status = Warn, Category = Existence, Code = PropertyExistenceApplicationEnforced, Subject = NODE:OrganizationSamplePerson(name), Message = Required property existence is checked by the provider but is not enforced by the observed database schema. }
- GraphSchemaIssue { Status = Warn, Category = PropertyType, Code = PropertyTypeApplicationEnforced, Subject = NODE:OrganizationSamplePerson(name), Message = Provider expects STRING; no property type constraint was observed. }
- GraphSchemaIssue { Status = Warn, Category = Capability, Code = ProposalCapabilityUnavailable, Subject = NODE:OrganizationSamplePerson(name), Message = ExistenceConstraint support is Unsupported; no DDL is proposed. }
- GraphSchemaIssue { Status = Warn, Category = Capability, Code = ProposalCapabilityUnavailable, Subject = NODE:OrganizationSamplePerson(name), Message = PropertyTypeConstraint support is Unsupported; no DDL is proposed. }
- GraphSchemaIssue { Status = Fail, Category = Identity, Code = MissingIdentityConstraint, Subject = RELATIONSHIP:ORGANIZATION_SAMPLE_WORKS_FOR(employmentId), Message = No exact node/relationship property uniqueness or key constraint covers these store properties. }
- GraphSchemaIssue { Status = Warn, Category = Existence, Code = PropertyExistenceApplicationEnforced, Subject = RELATIONSHIP:ORGANIZATION_SAMPLE_WORKS_FOR(employmentId), Message = Required property existence is checked by the provider but is not enforced by the observed database schema. }
- GraphSchemaIssue { Status = Warn, Category = PropertyType, Code = PropertyTypeApplicationEnforced, Subject = RELATIONSHIP:ORGANIZATION_SAMPLE_WORKS_FOR(employmentId), Message = Provider expects STRING; no property type constraint was observed. }
- GraphSchemaIssue { Status = Warn, Category = Capability, Code = ProposalCapabilityUnavailable, Subject = RELATIONSHIP:ORGANIZATION_SAMPLE_WORKS_FOR(employmentId), Message = ExistenceConstraint support is Unsupported; no DDL is proposed. }
- GraphSchemaIssue { Status = Warn, Category = Capability, Code = ProposalCapabilityUnavailable, Subject = RELATIONSHIP:ORGANIZATION_SAMPLE_WORKS_FOR(employmentId), Message = PropertyTypeConstraint support is Unsupported; no DDL is proposed. }
- GraphSchemaIssue { Status = Warn, Category = Existence, Code = PropertyExistenceApplicationEnforced, Subject = RELATIONSHIP:ORGANIZATION_SAMPLE_WORKS_FOR(role), Message = Required property existence is checked by the provider but is not enforced by the observed database schema. }
- GraphSchemaIssue { Status = Warn, Category = PropertyType, Code = PropertyTypeApplicationEnforced, Subject = RELATIONSHIP:ORGANIZATION_SAMPLE_WORKS_FOR(role), Message = Provider expects STRING; no property type constraint was observed. }
- GraphSchemaIssue { Status = Warn, Category = Capability, Code = ProposalCapabilityUnavailable, Subject = RELATIONSHIP:ORGANIZATION_SAMPLE_WORKS_FOR(role), Message = ExistenceConstraint support is Unsupported; no DDL is proposed. }
- GraphSchemaIssue { Status = Warn, Category = Capability, Code = ProposalCapabilityUnavailable, Subject = RELATIONSHIP:ORGANIZATION_SAMPLE_WORKS_FOR(role), Message = PropertyTypeConstraint support is Unsupported; no DDL is proposed. }
- GraphSchemaIssue { Status = Warn, Category = Existence, Code = PropertyExistenceApplicationEnforced, Subject = RELATIONSHIP:ORGANIZATION_SAMPLE_WORKS_FOR(validFrom), Message = Required property existence is checked by the provider but is not enforced by the observed database schema. }
- GraphSchemaIssue { Status = Warn, Category = PropertyType, Code = PropertyTypeApplicationEnforced, Subject = RELATIONSHIP:ORGANIZATION_SAMPLE_WORKS_FOR(validFrom), Message = Provider expects STRING; no property type constraint was observed. }
- GraphSchemaIssue { Status = Warn, Category = Capability, Code = ProposalCapabilityUnavailable, Subject = RELATIONSHIP:ORGANIZATION_SAMPLE_WORKS_FOR(validFrom), Message = ExistenceConstraint support is Unsupported; no DDL is proposed. }
- GraphSchemaIssue { Status = Warn, Category = Capability, Code = ProposalCapabilityUnavailable, Subject = RELATIONSHIP:ORGANIZATION_SAMPLE_WORKS_FOR(validFrom), Message = PropertyTypeConstraint support is Unsupported; no DDL is proposed. }
- GraphSchemaIssue { Status = Warn, Category = PropertyType, Code = PropertyTypeApplicationEnforced, Subject = RELATIONSHIP:ORGANIZATION_SAMPLE_WORKS_FOR(validTo), Message = Provider expects STRING; no property type constraint was observed. }
- GraphSchemaIssue { Status = Warn, Category = Capability, Code = ProposalCapabilityUnavailable, Subject = RELATIONSHIP:ORGANIZATION_SAMPLE_WORKS_FOR(validTo), Message = PropertyTypeConstraint support is Unsupported; no DDL is proposed. }
- GraphSchemaIssue { Status = Warn, Category = Manifest, Code = ManifestCaveat, Subject = manifest, Message = relationship:ORGANIZATION_SAMPLE_WORKS_FOR.validFrom: opaque conversion; equal fingerprints describe storage shape, not converter semantic equivalence. }
- GraphSchemaIssue { Status = Warn, Category = Manifest, Code = ManifestCaveat, Subject = manifest, Message = relationship:ORGANIZATION_SAMPLE_WORKS_FOR.validTo: opaque conversion; equal fingerprints describe storage shape, not converter semantic equivalence. }
- GraphSchemaIssue { Status = Warn, Category = Manifest, Code = ManifestCaveat, Subject = manifest, Message = relationship:ORGANIZATION_SAMPLE_WORKS_FOR: indexes containing endpoint slots are excluded; endpoint FK values are not stored relationship properties. }
