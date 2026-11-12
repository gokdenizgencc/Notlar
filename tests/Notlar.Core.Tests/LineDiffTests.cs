namespace Notlar.Core.Tests;

public sealed class LineDiffTests
{
    [Fact]
    public void Unchanged_lines_are_all_kept()
    {
        var ops = LineDiff.Map(["a", "b"], ["a", "b"]);

        Assert.Equal(
            [new LineOp(LineOpKind.Keep, 0, 0), new LineOp(LineOpKind.Keep, 1, 1)],
            ops);
    }

    [Fact]
    public void Edited_middle_line_becomes_update()
    {
        var ops = LineDiff.Map(["a", "b", "c"], ["a", "x", "c"]);

        Assert.Equal(
            [
                new LineOp(LineOpKind.Keep, 0, 0),
                new LineOp(LineOpKind.Update, 1, 1),
                new LineOp(LineOpKind.Keep, 2, 2),
            ],
            ops);
    }

    [Fact]
    public void Inserted_line_between_keeps_becomes_insert()
    {
        var ops = LineDiff.Map(["a", "c"], ["a", "b", "c"]);

        Assert.Equal(
            [
                new LineOp(LineOpKind.Keep, 0, 0),
                new LineOp(LineOpKind.Insert, NewIndex: 1),
                new LineOp(LineOpKind.Keep, 1, 2),
            ],
            ops);
    }

    [Fact]
    public void Deleted_line_between_keeps_becomes_delete()
    {
        var ops = LineDiff.Map(["a", "b", "c"], ["a", "c"]);

        Assert.Equal(
            [
                new LineOp(LineOpKind.Keep, 0, 0),
                new LineOp(LineOpKind.Delete, 1),
                new LineOp(LineOpKind.Keep, 2, 1),
            ],
            ops);
    }

    [Fact]
    public void Moved_line_is_treated_as_delete_plus_insert_not_a_move()
    {
        // "c" moves from the end to the front; the implementation has no move
        // detection, so it must show up as an insert (new c at the front) and a
        // delete (old c at the end), while "a"/"b" are recognised as kept.
        var ops = LineDiff.Map(["a", "b", "c"], ["c", "a", "b"]);

        Assert.Equal(
            [
                new LineOp(LineOpKind.Insert, NewIndex: 0),
                new LineOp(LineOpKind.Keep, 0, 1),
                new LineOp(LineOpKind.Keep, 1, 2),
                new LineOp(LineOpKind.Delete, 2),
            ],
            ops);
    }

    [Fact]
    public void Blank_new_lines_are_ignored()
    {
        var ops = LineDiff.Map(["a", "b"], ["a", "", "b", "  "]);

        Assert.Equal(
            [new LineOp(LineOpKind.Keep, 0, 0), new LineOp(LineOpKind.Keep, 1, 2)],
            ops);
    }

    [Fact]
    public void Empty_old_and_new_produce_no_ops()
    {
        Assert.Empty(LineDiff.Map([], []));
    }

    [Fact]
    public void All_new_lines_are_inserts_when_old_is_empty()
    {
        var ops = LineDiff.Map([], ["a", "b"]);

        Assert.Equal(
            [new LineOp(LineOpKind.Insert, NewIndex: 0), new LineOp(LineOpKind.Insert, NewIndex: 1)],
            ops);
    }

    [Fact]
    public void All_old_lines_are_deletes_when_new_is_empty()
    {
        var ops = LineDiff.Map(["a", "b"], []);

        Assert.Equal(
            [new LineOp(LineOpKind.Delete, 0), new LineOp(LineOpKind.Delete, 1)],
            ops);
    }

    [Fact]
    public void Splitting_a_line_with_enter_keeps_the_note_on_the_first_part()
    {
        // Notepad'de satır ortasında Enter: ilk parça mevcut notu güncellemeli, ikinci parça yeni not olmalı.
        var ops = LineDiff.Map(["toplantı notları", "b"], ["toplantı", "notları", "b"]);

        Assert.Equal(
            [
                new LineOp(LineOpKind.Update, 0, 0),
                new LineOp(LineOpKind.Insert, NewIndex: 1),
                new LineOp(LineOpKind.Keep, 1, 2),
            ],
            ops);
    }

    [Fact]
    public void Merging_lines_with_backspace_updates_the_first_and_deletes_the_second()
    {
        var ops = LineDiff.Map(["a", "bir", "iki", "z"], ["a", "biriki", "z"]);

        Assert.Equal(
            [
                new LineOp(LineOpKind.Keep, 0, 0),
                new LineOp(LineOpKind.Update, 1, 1),
                new LineOp(LineOpKind.Delete, 2),
                new LineOp(LineOpKind.Keep, 3, 2),
            ],
            ops);
    }

    [Fact]
    public void New_line_typed_above_an_edited_line_does_not_steal_its_note()
    {
        // "rapor" düzenlenip "rapor hazır" olurken üstüne yepyeni bir satır yazıldı: eski not
        // benzer olan satırla eşleşmeli (yazar/saat korunur), yeni satır ekleme olmalı.
        var ops = LineDiff.Map(["a", "rapor", "c"], ["a", "yeni iş", "rapor hazır", "c"]);

        Assert.Equal(
            [
                new LineOp(LineOpKind.Keep, 0, 0),
                new LineOp(LineOpKind.Insert, NewIndex: 1),
                new LineOp(LineOpKind.Update, 1, 2),
                new LineOp(LineOpKind.Keep, 2, 3),
            ],
            ops);
    }

    [Fact]
    public void Edited_line_deleted_next_to_another_edit_pairs_by_similarity()
    {
        // İki satırdan ilki silinip ikincisi düzenlendi: silinen, benzemeyen ilk satır olmalı.
        var ops = LineDiff.Map(["süt al", "ekmek al"], ["ekmek al!"]);

        Assert.Equal(
            [new LineOp(LineOpKind.Delete, 0), new LineOp(LineOpKind.Update, 1, 0)],
            ops);
    }

    [Fact]
    public void Gap_ops_are_emitted_in_line_order()
    {
        var ops = LineDiff.Map(["x1", "y1"], ["n1", "x2", "n2", "y2", "n3"]);

        var newIndices = ops.Where(o => o.NewIndex >= 0).Select(o => o.NewIndex).ToList();
        Assert.Equal(newIndices.OrderBy(i => i), newIndices);
        Assert.Equal(2, ops.Count(o => o.Kind == LineOpKind.Update));
        Assert.Equal(3, ops.Count(o => o.Kind == LineOpKind.Insert));
    }
}
