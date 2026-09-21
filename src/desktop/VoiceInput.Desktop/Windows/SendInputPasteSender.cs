using System.Runtime.InteropServices;

namespace VoiceInput.Desktop.Windows;

public sealed class SendInputPasteSender : IPasteSender
{
    private const uint KeyboardInput = 1;
    private const uint KeyUp = 0x0002;
    private const ushort VirtualKeyControl = 0x11;
    private const ushort VirtualKeyV = 0x56;

    public static int InputStructureSize => Marshal.SizeOf<Input>();

    public bool SendPaste()
    {
        var inputs = new[]
        {
            Key(VirtualKeyControl, 0),
            Key(VirtualKeyV, 0),
            Key(VirtualKeyV, KeyUp),
            Key(VirtualKeyControl, KeyUp),
        };

        return SendInput(
            checked((uint)inputs.Length),
            inputs,
            InputStructureSize) == inputs.Length;
    }

    private static Input Key(ushort virtualKey, uint flags) => new()
    {
        Type = KeyboardInput,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInputData
            {
                VirtualKey = virtualKey,
                Flags = flags,
            },
        },
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint inputCount,
        Input[] inputs,
        int inputSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInputData Mouse;

        [FieldOffset(0)]
        public KeyboardInputData Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputData
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputData
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }
}
