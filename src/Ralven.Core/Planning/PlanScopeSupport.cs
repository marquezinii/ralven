using Ralven.Contracts;

namespace Ralven.Core.Planning;

/// <summary>
/// Combinações de escopo e edição aceitas por um plano.
/// </summary>
/// <remarks>
/// A regra é um invariante de segurança: o GTAV Enhanced não tem suporte
/// operacional, então o escopo do FiveM só aceita a edição Legacy. Ela é
/// verificada tanto no runtime quanto no broker elevado, e vivia duplicada nos
/// dois — relaxar um lado sem o outro abriria exatamente a divergência que a
/// revalidação existe para impedir.
/// </remarks>
public static class PlanScopeSupport
{
    public static bool IsSupported(OptimizationScope scope, FiveMEdition edition) => scope switch
    {
        OptimizationScope.FiveMLegacy => edition == FiveMEdition.Legacy,
        OptimizationScope.GeneralWindows => Enum.IsDefined(edition),
        _ => false
    };
}
