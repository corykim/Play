public struct SongSelectEntrySelection
{
    public SongSelectEntry Entry { get; private set; }
    public int Index { get; private set; }
    public int Count { get; private set; }

    public SongSelectEntrySelection(SongSelectEntry entry, int index, int count)
    {
        Entry = entry;
        Index = index;
        Count = count;
    }
}