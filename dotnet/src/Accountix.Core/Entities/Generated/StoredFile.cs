using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class StoredFile
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string Key { get; set; } = null!;

    public string? ContentType { get; set; }

    public int Size { get; set; }

    public byte[] Data { get; set; } = null!;

    public DateTime? CreatedAt { get; set; }
}
