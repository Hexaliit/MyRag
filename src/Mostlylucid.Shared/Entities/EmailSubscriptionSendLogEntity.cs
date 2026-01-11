using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Mostlylucid.Shared.Entities;

public class EmailSubscriptionSendLogEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    [Column(TypeName = "varchar(24)")]

    public SubscriptionType SubscriptionType { get; set; }

    [Required] public DateTimeOffset LastSent { get; set; } = DateTimeOffset.Now;
}