using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace XRomConverter.Content;

internal sealed class PsarcArchive : IDisposable
{
	private sealed record Entry(int BlockIndex, long Size, long Offset);

	private readonly FileStream mStream;

	private readonly int mBlockSize;

	private readonly int[] mBlockLengths;

	private readonly Dictionary<string, Entry> mEntries;

	public int FileCount => mEntries.Count;

	public PsarcArchive(string filePath)
	{
		mStream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
		try
		{
			using BinaryReader binaryReader = new BinaryReader(mStream, Encoding.UTF8, leaveOpen: true);
			if (Encoding.ASCII.GetString(binaryReader.ReadBytes(4)) != "PSAR")
			{
				throw new InvalidDataException(filePath + " is not a PSARC archive.");
			}
			ReadUInt32BigEndian(binaryReader);
			string text = Encoding.ASCII.GetString(binaryReader.ReadBytes(4));
			int num3;
			List<Entry> list;
			int num4;
			int blockLengthWidth;
			checked
			{
				int num = (int)ReadUInt32BigEndian(binaryReader);
				int num2 = (int)ReadUInt32BigEndian(binaryReader);
				num3 = (int)ReadUInt32BigEndian(binaryReader);
				mBlockSize = (int)ReadUInt32BigEndian(binaryReader);
				ReadUInt32BigEndian(binaryReader);
				if (text != "zlib" || num2 != 30 || num3 < 1 || mBlockSize < 1)
				{
					throw new InvalidDataException("Unsupported PSARC variant.");
				}
				list = new List<Entry>(num3);
				for (int i = 0; i < num3; i = unchecked(i + 1))
				{
					if (binaryReader.ReadBytes(16).Length != 16)
					{
						throw new EndOfStreamException();
					}
					int blockIndex = (int)ReadUInt32BigEndian(binaryReader);
					long size = (long)ReadUnsignedBigEndian(binaryReader, 5);
					long offset = (long)ReadUnsignedBigEndian(binaryReader, 5);
					list.Add(new Entry(blockIndex, size, offset));
				}
				num4 = num - 32 - num3 * num2;
				blockLengthWidth = GetBlockLengthWidth(mBlockSize);
			}
			if (num4 < 0 || num4 % blockLengthWidth != 0)
			{
				throw new InvalidDataException("Invalid PSARC block table.");
			}
			mBlockLengths = new int[num4 / blockLengthWidth];
			for (int j = 0; j < mBlockLengths.Length; j++)
			{
				mBlockLengths[j] = checked((int)ReadUnsignedBigEndian(binaryReader, blockLengthWidth));
			}
			string[] array = (from x in Encoding.UTF8.GetString(ReadEntry(list[0])).TrimEnd('\0').Split('\n')
				select x.TrimEnd('\r') into x
				where x.Length > 0
				select x).ToArray();
			if (array.Length != num3 - 1)
			{
				throw new InvalidDataException($"PSARC manifest has {array.Length} names for {num3 - 1} entries.");
			}
			mEntries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
			for (int num5 = 0; num5 < array.Length; num5++)
			{
				mEntries[array[num5].Replace('\\', '/')] = list[num5 + 1];
			}
		}
		catch
		{
			mStream.Dispose();
			throw;
		}
	}

	public bool TryOpen(string logicalPath, out Stream? stream)
	{
		string text = logicalPath.Replace('\\', '/').TrimStart('/');
		if (!mEntries.TryGetValue(text, out Entry value))
		{
			string key = text.Substring(text.LastIndexOf('/') + 1);
			if (!mEntries.TryGetValue(key, out value))
			{
				stream = null;
				return false;
			}
		}
		stream = new MemoryStream(ReadEntry(value), writable: false);
		return true;
	}

	private byte[] ReadEntry(Entry entry)
	{
		if (entry.Size > int.MaxValue)
		{
			throw new InvalidDataException("PSARC entry is too large to load in memory.");
		}
		mStream.Position = entry.Offset;
		checked
		{
			using MemoryStream memoryStream = new MemoryStream((int)entry.Size);
			long num = entry.Size;
			int num2 = (int)unchecked(checked(entry.Size + mBlockSize - 1) / mBlockSize);
			for (int i = 0; i < num2; i = unchecked(i + 1))
			{
				int num3 = (int)Math.Min(mBlockSize, num);
				int num4 = entry.BlockIndex + i;
				unchecked
				{
					if ((uint)num4 >= (uint)mBlockLengths.Length)
					{
						throw new InvalidDataException("PSARC block index is outside the block table.");
					}
					int num5 = mBlockLengths[num4];
					byte[] array = new byte[(num5 == 0) ? num3 : num5];
					mStream.ReadExactly(array);
					if (num5 == 0)
					{
						memoryStream.Write(array, 0, num3);
					}
					else
					{
						byte[] array2;
						try
						{
							using MemoryStream stream = new MemoryStream(array, writable: false);
							using ZLibStream zLibStream = new ZLibStream(stream, CompressionMode.Decompress);
							using MemoryStream memoryStream2 = new MemoryStream(num3);
							zLibStream.CopyTo(memoryStream2);
							array2 = memoryStream2.ToArray();
						}
						catch (InvalidDataException)
						{
							array2 = array;
						}
						if (array2.Length < num3)
						{
							throw new InvalidDataException("PSARC block is shorter than expected.");
						}
						memoryStream.Write(array2, 0, num3);
					}
					num -= num3;
				}
			}
			return memoryStream.ToArray();
		}
	}

	private static int GetBlockLengthWidth(int blockSize)
	{
		int num = 1;
		uint num2 = checked((uint)(blockSize - 1));
		while (num2 > 255)
		{
			num2 >>= 8;
			num++;
		}
		return num;
	}

	private static uint ReadUInt32BigEndian(BinaryReader reader)
	{
		byte[] array = reader.ReadBytes(4);
		if (array.Length != 4)
		{
			throw new EndOfStreamException();
		}
		return BinaryPrimitives.ReadUInt32BigEndian(array);
	}

	private static ulong ReadUnsignedBigEndian(BinaryReader reader, int width)
	{
		ulong num = 0uL;
		for (int i = 0; i < width; i++)
		{
			num = (num << 8) | reader.ReadByte();
		}
		return num;
	}

	public void Dispose()
	{
		mStream.Dispose();
	}
}
