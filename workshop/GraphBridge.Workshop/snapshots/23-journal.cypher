CREATE CONSTRAINT `gb_migration_control_key` FOR (n:`__GraphBridgeMigrationControl`) REQUIRE n.`id` IS UNIQUE
CREATE CONSTRAINT `gb_migration_history_key` FOR (n:`__GraphBridgeMigration`) REQUIRE n.`id` IS UNIQUE
CREATE CONSTRAINT `gb_migration_step_key` FOR (n:`__GraphBridgeMigrationStep`) REQUIRE n.`id` IS UNIQUE
