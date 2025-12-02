using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using QuestPDF.Drawing;
using QuestPDF.Elements;
using BarangayProject.Models.AdminModel; // adjust if your ApplicationUser/Sitio live in other namespace

namespace BarangayProject
{
    public class PdfReports : IDocument
    {
        private readonly IReadOnlyList<ApplicationUser> _users;
        private readonly IReadOnlyDictionary<int, Sitio> _sitioMap;
        private readonly IReadOnlyDictionary<string, string> _rolesMap;
        private readonly string _title;

        public PdfReports(IReadOnlyList<ApplicationUser> users, IReadOnlyDictionary<int, Sitio> sitioMap, IReadOnlyDictionary<string, string> rolesMap, string title = "Barangay System Reports")
        {
            _users = users ?? Array.Empty<ApplicationUser>();
            _sitioMap = sitioMap ?? new Dictionary<int, Sitio>();
            _rolesMap = rolesMap ?? new Dictionary<string, string>();
            _title = title ?? "Barangay System Reports";
        }

        public DocumentMetadata GetMetadata() => DocumentMetadata.Default;

        public void Compose(IDocumentContainer container)
        {
            // Page 1: Users
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(40);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(x => x.FontSize(11).FontColor(Colors.Black));

                page.Header().Element(ComposeHeader);

                page.Content().PaddingTop(10).Element(ComposeUsersPage);

                page.Footer().AlignCenter().Text(txt =>
                {
                    txt.Span("Page ");
                    txt.CurrentPageNumber();
                    txt.Span(" of ");
                    txt.TotalPages();
                });
            });

            // Page 2: Sitios
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(40);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(x => x.FontSize(11).FontColor(Colors.Black));

                page.Header().Element(ComposeHeader);

                page.Content().PaddingTop(10).Element(ComposeSitioPage);

                page.Footer().AlignCenter().Text(txt =>
                {
                    txt.Span("Page ");
                    txt.CurrentPageNumber();
                    txt.Span(" of ");
                    txt.TotalPages();
                });
            });
        }

        private void ComposeHeader(IContainer container)
        {
            // header should be single-child container usage
            container.Column(col =>
            {
                col.Item().Row(row =>
                {
                    row.RelativeColumn().Column(c =>
                    {
                        c.Item().Text(_title).FontSize(20).Bold();
                        c.Item().Text($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm}").FontSize(9).FontColor(Colors.Grey.Darken1);
                    });

                    row.ConstantColumn(140)
                        .AlignRight()
                        .Text("Sta. Isabel").FontSize(12);
                });

                col.Item().PaddingTop(6).Element(ct => ct.LineHorizontal(1).LineColor(Colors.Grey.Medium));
            });
        }

        private void ComposeUsersPage(IContainer container)
        {
            container.Column(column =>
            {
                column.Spacing(8);

                column.Item().Text("Users Report").FontSize(16).Bold();

                // table
                column.Item().Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.ConstantColumn(60);    // User ID
                        columns.RelativeColumn(3);     // Email
                        columns.RelativeColumn(3);     // Name
                        columns.RelativeColumn(2);     // Roles
                        columns.ConstantColumn(80);    // Joined
                    });

                    // header row
                    table.Header(header =>
                    {
                        header.Cell().Background(Colors.Grey.Lighten3).Padding(6).Text("User ID").Bold();
                        header.Cell().Background(Colors.Grey.Lighten3).Padding(6).Text("Email").Bold();
                        header.Cell().Background(Colors.Grey.Lighten3).Padding(6).Text("Name").Bold();
                        header.Cell().Background(Colors.Grey.Lighten3).Padding(6).Text("Roles").Bold();
                        header.Cell().Background(Colors.Grey.Lighten3).Padding(6).Text("Joined").Bold();
                    });

                    // rows: use index to create sequential User ID identical to ManageUsers ordering
                    for (int i = 0; i < _users.Count; i++)
                    {
                        var u = _users[i];
                        var profile = u.Profile;

                        // User ID: prefer profile.UserNumber if present, otherwise generate sequential number from position (1-based)
                        string userIdDisplay = profile?.UserNumber.HasValue == true
                            ? profile.UserNumber.Value.ToString()
                            : (i + 1).ToString();

                        var email = u.Email ?? "";
                        var fullName = profile != null
                            ? $"{(profile.FirstName ?? "").Trim()} {(profile.LastName ?? "").Trim()}".Trim()
                            : (u.DisplayName ?? u.UserName ?? "");

                        // roles from map if provided
                        string roles = "";
                        if (!string.IsNullOrWhiteSpace(u.Id) && _rolesMap.TryGetValue(u.Id, out var r))
                            roles = r ?? "";

                        // joined: prefer profile.CreatedAt then user.CreatedAt
                        var joined = "";
                        if (profile != null && profile.CreatedAt != default)
                            joined = profile.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd");
                        else if (u.CreatedAt != default)
                            joined = u.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd");

                        table.Cell().Padding(6).Text(userIdDisplay);
                        table.Cell().Padding(6).Text(email);
                        table.Cell().Padding(6).Text(fullName);
                        table.Cell().Padding(6).Text(roles);
                        table.Cell().Padding(6).Text(joined);
                    }
                });
            });
        }

        private void ComposeSitioPage(IContainer container)
        {
            container.Column(column =>
            {
                column.Spacing(8);
                column.Item().Text("Sitio Report").FontSize(16).Bold();

                var sitios = _sitioMap.Values.OrderBy(s => s.Id).ToList();

                column.Item().Table(table =>
                {
                    table.ColumnsDefinition(cols =>
                    {
                        cols.ConstantColumn(60);   // Sitio ID
                        cols.RelativeColumn(3);    // Sitio Name
                        cols.RelativeColumn(2);    // Assigned BHW
                    });

                    table.Header(header =>
                    {
                        header.Cell().Background(Colors.Grey.Lighten3).Padding(6).Text("Sitio ID").Bold();
                        header.Cell().Background(Colors.Grey.Lighten3).Padding(6).Text("Sitio Name").Bold();
                        header.Cell().Background(Colors.Grey.Lighten3).Padding(6).Text("Assigned BHW").Bold();
                    });

                    for (int i = 0; i < sitios.Count; i++)
                    {
                        var s = sitios[i];
                        var assigned = s.AssignedBhw != null
                            ? (!string.IsNullOrWhiteSpace(s.AssignedBhw.DisplayName)
                                ? s.AssignedBhw.DisplayName
                                : s.AssignedBhw.UserName)
                            : (s.AssignedBhwId ?? "");

                        table.Cell().Padding(6).Text((i + 1).ToString());
                        table.Cell().Padding(6).Text(s.Name ?? "");
                        table.Cell().Padding(6).Text(assigned);
                    }
                });
            });
        }


        /// <summary>
        /// Generate byte[] PDF
        /// </summary>
        public byte[] GeneratePdf()
        {
            using var ms = new MemoryStream();
            // If your QuestPDF needs licensing call here (uncomment if needed)
            // QuestPDF.Settings.License = QuestPDF.LicenseType.Community;
            var doc = Document.Create(container => Compose(container));
            doc.GeneratePdf(ms);
            return ms.ToArray();
        }
    }
}
