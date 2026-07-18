namespace EcmisWitness.Api.Domain;

public static class WitnessOpinionPolicy
{
    private static readonly IReadOnlySet<(int FormNumber, string Purpose)> RequiredOpinions =
        new HashSet<(int FormNumber, string Purpose)>
        {
            (4, "ผู้บังคับบัญชาชั้นต้น"),
            (4, "ผู้อำนวยการสำนัก/กอง"),
            (6, "ผู้บังคับบัญชาชั้นต้น"),
            (6, "ผู้อำนวยการสำนัก/กอง"),
            (6, "ผู้มีอำนาจจาก External Module"),
            (14, "ผู้บังคับบัญชาชั้นต้น"),
            (14, "ผู้อำนวยการสำนัก/กอง"),
            (14, "ผู้มีอำนาจจาก External Module"),
            (15, "ผู้อำนวยการสำนัก/กอง"),
            (15, "ผู้มีอำนาจจาก External Module")
        };

    public static bool RequiresOpinion(int formNumber, string purpose)
        => RequiredOpinions.Contains((formNumber, purpose));
}
