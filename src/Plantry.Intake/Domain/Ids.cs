namespace Plantry.Intake.Domain;

public readonly record struct ImportSessionId(Guid Value)
{
    public static ImportSessionId New() => new(Guid.CreateVersion7());
    public static ImportSessionId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}

public readonly record struct ImportLineId(Guid Value)
{
    public static ImportLineId New() => new(Guid.CreateVersion7());
    public static ImportLineId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
}
