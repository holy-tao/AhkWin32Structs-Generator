// Architecture flags from the metadata

[Flags]
public enum Architecture : uint
{
	None = 0,
	X86 = 1,
	X64 = 2,
	Arm64 = 4,
	All = 7
}