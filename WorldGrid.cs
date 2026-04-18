using Godot;

/// <summary>
/// Общие утилиты для работы с тайловой сеткой: преобразования координат
/// мир ↔ клетка, проверка проходимости, создание L8-текстур по байтовому
/// массиву. Раньше всё это было продублировано в каждой колонии/поле; теперь
/// все обращаются сюда.
///
/// Класс полностью статичный, без состояния — потокобезопасен и не аллоцирует
/// объектов в горячих циклах.
/// </summary>
public static class WorldGrid
{
    /// <summary>
    /// Координаты клетки по мировым координатам. Использует Floor для
    /// корректной работы с отрицательными значениями (мир за рамкой -1..-1).
    /// </summary>
    public static Vector2I WorldToCell(Vector2 pos, int tilePx)
        => new Vector2I((int)Mathf.Floor(pos.X / tilePx),
                        (int)Mathf.Floor(pos.Y / tilePx));

    /// <summary>Центр клетки в мировых координатах.</summary>
    public static Vector2 CellToWorld(Vector2I cell, int tilePx)
        => new Vector2(cell.X * tilePx + tilePx * 0.5f,
                       cell.Y * tilePx + tilePx * 0.5f);

    /// <summary>Проверка границ карты Width × Height (cell ∈ [0; W) × [0; H)).</summary>
    public static bool InBounds(Vector2I cell, int width, int height)
        => cell.X >= 0 && cell.X < width && cell.Y >= 0 && cell.Y < height;

    /// <summary>
    /// Универсальная проверка «клетка непроходима». Любая ссылка может быть
    /// null — тогда соответствующее препятствие просто не учитывается. Out-
    /// of-bounds трактуется как непроходимое (предотвращает вылет существ
    /// в пустоту за пределами рамки).
    /// </summary>
    public static bool IsBlocked(Vector2I cell, int width, int height,
        RockField rocks, TileMapLayer solidWalls, CrystalField crystals)
    {
        if (!InBounds(cell, width, height)) return true;
        if (rocks != null && rocks.HasRock(cell)) return true;
        if (solidWalls != null && solidWalls.GetCellSourceId(cell) >= 0) return true;
        if (crystals != null && crystals.IsMature(cell)) return true;
        return false;
    }

    /// <summary>Создаёт пару (Image, ImageTexture) для L8-данных.</summary>
    public static (Image image, ImageTexture texture) MakeL8Texture(int width, int height, byte[] data)
    {
        var img = Image.CreateFromData(width, height, false, Image.Format.L8, data);
        var tex = ImageTexture.CreateFromImage(img);
        return (img, tex);
    }

    /// <summary>Обновляет существующую текстуру новыми данными того же
    /// размера. Используется когда массив переписан inplace.</summary>
    public static void UpdateL8Texture(Image image, ImageTexture texture,
        int width, int height, byte[] data)
    {
        if (image == null || texture == null) return;
        image.SetData(width, height, false, Image.Format.L8, data);
        texture.Update(image);
    }
}
