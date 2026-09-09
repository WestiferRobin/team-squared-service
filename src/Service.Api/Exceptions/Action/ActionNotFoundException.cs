namespace Service.Api.Exceptions.Action;

public sealed class ActionNotFoundException : NotFoundException
{
    public Guid ActionId { get; }

    public ActionNotFoundException(Guid actionId)
        : base("The requested Action was not found.")
    {
        ActionId = actionId;
    }
}
