-- Alembic 0017_dotnet_data_protection, ported: the table the .NET app keeps
-- its Data Protection key ring in (antiforgery tokens and the flash cookie,
-- shared by every instance and slot; Web.Shared/Infrastructure/
-- DataProtectionStore.cs). Created only when missing, with the DDL Alembic
-- emitted, so the result is the same table.
DO $migration$
BEGIN
    IF to_regclass('public.dotnet_data_protection_keys') IS NULL THEN
        CREATE TABLE public.dotnet_data_protection_keys (
            id SERIAL NOT NULL,
            friendly_name VARCHAR(200) NOT NULL,
            xml TEXT NOT NULL,
            created_at TIMESTAMP WITHOUT TIME ZONE NOT NULL,
            PRIMARY KEY (id),
            CONSTRAINT uq_dotnet_dp_keys_friendly_name UNIQUE (friendly_name)
        );
    END IF;
END
$migration$;
