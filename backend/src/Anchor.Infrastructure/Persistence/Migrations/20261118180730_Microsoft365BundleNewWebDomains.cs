using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Anchor.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds Microsoft 365's newer web domains to a deployment's existing
    /// Microsoft 365 bundle (#531). Microsoft 365 on the web moved to
    /// <c>*.cloud.microsoft</c> (www.office.com and www.microsoft365.com now
    /// redirect to m365.cloud.microsoft; Outlook, Teams, Word, Excel,
    /// PowerPoint, OneDrive, OneNote and Forms have <c>*.cloud.microsoft</c>
    /// hosts), so a student in a session with that bundle got the block page on
    /// the Microsoft 365 home page. The seeds (DevDataSeeder,
    /// scripts/seed-bundles.ps1) list both domains now, but they only insert a
    /// bundle that is missing, so an existing bundle gets them here.
    /// <para>
    /// Only adds: the bundle keeps every entry it has, including any a teacher
    /// or admin added, and a domain it already lists isn't added twice. The
    /// target is the catalogue's live Microsoft 365 bundle (not archived, latest
    /// version); a deployment without one (a fresh database, before
    /// seed-bundles.ps1 runs) is left alone. The bundle's version goes up, as
    /// an edit in the catalogue editor would do it.
    /// </para>
    /// <para>
    /// The SQL is plain enough for SQL Server, which the deploy pipeline runs it
    /// on, and SQLite, which the backend tests run it on. Each added entry has a
    /// fixed id, so the version bump and Down find exactly the rows Up added.
    /// </para>
    /// </summary>
    public partial class Microsoft365BundleNewWebDomains : Migration
    {
        private const string CloudMicrosoftEntryId = "6DA699AA-B296-4546-BEE2-7E72AECF1A93";
        private const string Microsoft365ComEntryId = "60A2B00F-4590-450B-AB07-9443ADE49DBF";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                INSERT INTO BundleEntries (Id, BundleId, Kind, Value, MatchType)
                SELECT added.Id, b.Id, 'Domain', added.Value, 'Wildcard'
                FROM Bundles b
                CROSS JOIN (
                    SELECT '{CloudMicrosoftEntryId}' AS Id, '*.cloud.microsoft' AS Value
                    UNION ALL
                    SELECT '{Microsoft365ComEntryId}', '*.microsoft365.com'
                ) added
                WHERE b.Name = 'Microsoft 365'
                  AND b.IsArchived = 0
                  AND b.Version = (
                      SELECT MAX(live.Version) FROM Bundles live
                      WHERE live.Name = 'Microsoft 365' AND live.IsArchived = 0)
                  AND NOT EXISTS (
                      SELECT 1 FROM BundleEntries e
                      WHERE e.BundleId = b.Id
                        AND e.Kind = 'Domain'
                        AND LOWER(e.Value) = added.Value);
                """);

            // Above every version of the name, archived ones included, so the
            // bump can't collide with the (Name, Version) unique index.
            migrationBuilder.Sql($"""
                UPDATE Bundles
                SET Version = (SELECT MAX(same.Version) FROM Bundles same WHERE same.Name = Bundles.Name) + 1
                WHERE Id IN (
                    SELECT e.BundleId FROM BundleEntries e
                    WHERE e.Id IN ('{CloudMicrosoftEntryId}', '{Microsoft365ComEntryId}'));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Only the rows Up added, and only while the catalogue still has
            // them: saving the bundle in the editor re-creates its entries with
            // new ids, after which they are the admin's. The version stays.
            migrationBuilder.Sql($"""
                DELETE FROM BundleEntries
                WHERE Id IN ('{CloudMicrosoftEntryId}', '{Microsoft365ComEntryId}');
                """);
        }
    }
}
