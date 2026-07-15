using System.Buffers.Binary;

namespace PdfBox.Net.Layout;

internal static class SfntWebFontNormalizer
{
    private const uint ChecksumMagic = 0xB1B0AFBA;
    private const uint DsigTag = 0x44534947;
    private const uint HeadTag = 0x68656164;
    private const int OffsetTableSize = 12;
    private const int TableRecordSize = 16;
    private const int ChecksumAdjustmentOffset = 8;
    private const int ChecksumAdjustmentEnd = ChecksumAdjustmentOffset + sizeof(uint);
    private const int MaximumTableCount = ushort.MaxValue / TableRecordSize;

    public static bool TryNormalize(
        byte[] data,
        out byte[] normalizedData,
        out string? failureReason)
    {
        ArgumentNullException.ThrowIfNull(data);

        normalizedData = [];
        failureReason = null;
        if (data.Length < OffsetTableSize)
        {
            failureReason = "The sfnt offset table is truncated.";
            return false;
        }

        ushort tableCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4, sizeof(ushort)));
        if (tableCount == 0)
        {
            failureReason = "The sfnt contains no tables.";
            return false;
        }

        if (tableCount > MaximumTableCount)
        {
            failureReason = $"The sfnt declares too many tables ({tableCount}).";
            return false;
        }

        int directoryLength = OffsetTableSize + tableCount * TableRecordSize;
        if (directoryLength > data.Length)
        {
            failureReason = "The sfnt table directory is truncated.";
            return false;
        }

        TableRecord[] records = new TableRecord[tableCount];
        HashSet<uint> tags = [];
        bool needsNormalization = !HasExpectedSearchFields(data, tableCount) || data.Length % sizeof(uint) != 0;
        bool directoryIsSorted = true;
        uint previousTag = 0;
        TableRecord? headRecord = null;
        bool hasDigitalSignature = false;

        for (int index = 0; index < tableCount; index++)
        {
            ReadOnlySpan<byte> recordData = data.AsSpan(OffsetTableSize + index * TableRecordSize, TableRecordSize);
            uint tag = BinaryPrimitives.ReadUInt32BigEndian(recordData);
            uint storedChecksum = BinaryPrimitives.ReadUInt32BigEndian(recordData[4..]);
            uint offsetValue = BinaryPrimitives.ReadUInt32BigEndian(recordData[8..]);
            uint lengthValue = BinaryPrimitives.ReadUInt32BigEndian(recordData[12..]);
            if (offsetValue > int.MaxValue || lengthValue > int.MaxValue)
            {
                failureReason = $"Sfnt table {FormatTag(tag)} exceeds supported bounds.";
                return false;
            }

            int offset = (int)offsetValue;
            int length = (int)lengthValue;
            if (offset < directoryLength && length != 0)
            {
                failureReason = $"Sfnt table {FormatTag(tag)} overlaps the table directory.";
                return false;
            }

            if (offset > data.Length || length > data.Length - offset)
            {
                failureReason = $"Sfnt table {FormatTag(tag)} extends outside the font data.";
                return false;
            }

            if (!tags.Add(tag))
            {
                failureReason = $"The sfnt contains duplicate {FormatTag(tag)} tables.";
                return false;
            }

            if (index > 0 && tag <= previousTag)
            {
                directoryIsSorted = false;
            }

            previousTag = tag;
            TableRecord record = new(tag, offset, length);
            records[index] = record;
            needsNormalization |= offset % sizeof(uint) != 0;
            uint calculatedChecksum = CalculateChecksum(
                data.AsSpan(offset, length),
                zeroChecksumAdjustment: tag == HeadTag);
            needsNormalization |= calculatedChecksum != storedChecksum;

            if (tag == HeadTag)
            {
                headRecord = record;
            }
            else if (tag == DsigTag)
            {
                hasDigitalSignature = true;
            }
        }

        if (!directoryIsSorted)
        {
            needsNormalization = true;
        }

        if (!ValidateTableRanges(records, out failureReason))
        {
            return false;
        }

        if (headRecord is not TableRecord head || head.Length < ChecksumAdjustmentEnd)
        {
            failureReason = "The sfnt head table is missing or truncated.";
            return false;
        }

        needsNormalization |= CalculateChecksum(data, zeroChecksumAdjustment: false) != ChecksumMagic;
        if (!needsNormalization)
        {
            normalizedData = data;
            return true;
        }

        if (hasDigitalSignature)
        {
            failureReason = "The malformed sfnt contains a DSIG table and cannot be safely repacked.";
            return false;
        }

        if (!TryCalculateOutputLength(records, out int outputLength))
        {
            failureReason = "The normalized sfnt would exceed supported bounds.";
            return false;
        }

        normalizedData = Repack(data, records, outputLength);
        return true;
    }

    private static bool ValidateTableRanges(TableRecord[] records, out string? failureReason)
    {
        TableRecord[] ordered = records
            .Where(static record => record.Length != 0)
            .OrderBy(static record => record.Offset)
            .ToArray();
        int previousEnd = 0;
        uint previousTag = 0;
        foreach (TableRecord record in ordered)
        {
            if (record.Offset < previousEnd)
            {
                failureReason = $"Sfnt tables {FormatTag(previousTag)} and {FormatTag(record.Tag)} overlap.";
                return false;
            }

            previousEnd = record.Offset + record.Length;
            previousTag = record.Tag;
        }

        failureReason = null;
        return true;
    }

    private static bool TryCalculateOutputLength(TableRecord[] records, out int outputLength)
    {
        long length = OffsetTableSize + records.Length * TableRecordSize;
        foreach (TableRecord record in records)
        {
            length = AlignToUInt32(length) + AlignToUInt32((long)record.Length);
            if (length > int.MaxValue)
            {
                outputLength = 0;
                return false;
            }
        }

        outputLength = (int)length;
        return true;
    }

    private static byte[] Repack(byte[] data, TableRecord[] records, int outputLength)
    {
        TableRecord[] ordered = records.OrderBy(static record => record.Tag).ToArray();
        byte[] output = new byte[outputLength];
        data.AsSpan(0, sizeof(uint)).CopyTo(output);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(4), (ushort)ordered.Length);
        WriteSearchFields(output, ordered.Length);

        int outputOffset = OffsetTableSize + ordered.Length * TableRecordSize;
        int headOutputOffset = -1;
        for (int index = 0; index < ordered.Length; index++)
        {
            TableRecord record = ordered[index];
            outputOffset = AlignToUInt32(outputOffset);
            data.AsSpan(record.Offset, record.Length).CopyTo(output.AsSpan(outputOffset));
            if (record.Tag == HeadTag)
            {
                output.AsSpan(outputOffset + ChecksumAdjustmentOffset, sizeof(uint)).Clear();
                headOutputOffset = outputOffset;
            }

            Span<byte> outputRecord = output.AsSpan(OffsetTableSize + index * TableRecordSize, TableRecordSize);
            BinaryPrimitives.WriteUInt32BigEndian(outputRecord, record.Tag);
            BinaryPrimitives.WriteUInt32BigEndian(
                outputRecord[4..],
                CalculateChecksum(output.AsSpan(outputOffset, record.Length), zeroChecksumAdjustment: false));
            BinaryPrimitives.WriteUInt32BigEndian(outputRecord[8..], (uint)outputOffset);
            BinaryPrimitives.WriteUInt32BigEndian(outputRecord[12..], (uint)record.Length);
            outputOffset += AlignToUInt32(record.Length);
        }

        uint checksumAdjustment = unchecked(
            ChecksumMagic - CalculateChecksum(output, zeroChecksumAdjustment: false));
        BinaryPrimitives.WriteUInt32BigEndian(
            output.AsSpan(headOutputOffset + ChecksumAdjustmentOffset, sizeof(uint)),
            checksumAdjustment);
        return output;
    }

    private static bool HasExpectedSearchFields(ReadOnlySpan<byte> data, int tableCount)
    {
        SearchFields expected = CalculateSearchFields(tableCount);
        return BinaryPrimitives.ReadUInt16BigEndian(data[6..]) == expected.SearchRange &&
               BinaryPrimitives.ReadUInt16BigEndian(data[8..]) == expected.EntrySelector &&
               BinaryPrimitives.ReadUInt16BigEndian(data[10..]) == expected.RangeShift;
    }

    private static void WriteSearchFields(Span<byte> output, int tableCount)
    {
        SearchFields fields = CalculateSearchFields(tableCount);
        BinaryPrimitives.WriteUInt16BigEndian(output[6..], fields.SearchRange);
        BinaryPrimitives.WriteUInt16BigEndian(output[8..], fields.EntrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(output[10..], fields.RangeShift);
    }

    private static SearchFields CalculateSearchFields(int tableCount)
    {
        int maximumPowerOfTwo = 1;
        ushort entrySelector = 0;
        while (maximumPowerOfTwo * 2 <= tableCount)
        {
            maximumPowerOfTwo *= 2;
            entrySelector++;
        }

        ushort searchRange = (ushort)(maximumPowerOfTwo * TableRecordSize);
        ushort rangeShift = (ushort)(tableCount * TableRecordSize - searchRange);
        return new SearchFields(searchRange, entrySelector, rangeShift);
    }

    private static uint CalculateChecksum(ReadOnlySpan<byte> data, bool zeroChecksumAdjustment)
    {
        uint checksum = 0;
        int offset = 0;
        while (offset + sizeof(uint) <= data.Length)
        {
            uint value = zeroChecksumAdjustment && offset == ChecksumAdjustmentOffset
                ? 0
                : BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
            checksum = unchecked(checksum + value);
            offset += sizeof(uint);
        }

        if (offset < data.Length)
        {
            Span<byte> finalWord = stackalloc byte[sizeof(uint)];
            data[offset..].CopyTo(finalWord);
            checksum = unchecked(checksum + BinaryPrimitives.ReadUInt32BigEndian(finalWord));
        }

        return checksum;
    }

    private static int AlignToUInt32(int value) => checked((value + sizeof(uint) - 1) & -sizeof(uint));

    private static long AlignToUInt32(long value) => (value + sizeof(uint) - 1) & -sizeof(uint);

    private static string FormatTag(uint tag) => $"0x{tag:X8}";

    private readonly record struct SearchFields(
        ushort SearchRange,
        ushort EntrySelector,
        ushort RangeShift);

    private readonly record struct TableRecord(
        uint Tag,
        int Offset,
        int Length);
}
