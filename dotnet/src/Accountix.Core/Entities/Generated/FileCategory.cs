using System;
using System.Collections.Generic;

namespace Accountix.Core.Entities;

public partial class FileCategory
{
    public int Id { get; set; }

    public int? CompanyId { get; set; }

    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    public virtual ICollection<DigitalFile> DigitalFiles { get; set; } = new List<DigitalFile>();
}
