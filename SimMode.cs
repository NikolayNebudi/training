/// <summary>
/// Глобальный флаг режима симуляции (headless). Когда активен, экосистемные
/// системы:
///   - НЕ создают визуалов (Sprite2D, MultiMesh, ImageTexture, шейдеры);
///   - НЕ выполняют инкрементальную «покраску» тайлов в TileMapLayer;
///   - игнорируют автовызовы <c>_Process</c> от движка — тики совершает
///     только <see cref="SimRunner"/>, выставляя <see cref="AllowProcess"/>
///     на время своего пакетного цикла.
///
/// Логика игры (популяции, рост мха, рост кристаллов, охота червей и т.д.)
/// работает идентично — потому что вся она оперирует внутренними byte[]
/// массивами и структурами, а не визуалами.
/// </summary>
public static class SimMode
{
    /// <summary>true в headless-режиме (Sim.tscn). По умолчанию false (Main.tscn).</summary>
    public static bool Headless = false;

    /// <summary>Когда true — sim-системам разрешено выполнять _Process.
    /// SimRunner устанавливает в true только на время своего пакетного
    /// цикла, иначе движок мог бы дополнительно тикать систему с
    /// неконтролируемым delta.</summary>
    public static bool AllowProcess = false;

    /// <summary>Удобный шорткат: «работать только если включён real-game
    /// режим, ИЛИ если SimRunner явно разрешил пакетный шаг».</summary>
    public static bool ShouldProcess => !Headless || AllowProcess;
}
