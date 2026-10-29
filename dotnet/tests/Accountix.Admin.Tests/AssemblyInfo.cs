// Every test makes and drops its own databases (st_admin_*): one at a time keeps the shared
// Postgres well under its 100 connections.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
