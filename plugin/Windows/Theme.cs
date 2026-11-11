using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace GilDom.Windows;

internal static class Theme
{
    private static readonly (ImGuiCol Column, Vector4 Color)[] Colors =
    [
        (ImGuiCol.Text, new(0.98f, 0.93f, 0.95f, 1f)),
        (ImGuiCol.TextDisabled, new(0.72f, 0.52f, 0.60f, 1f)),
        (ImGuiCol.TextSelectedBg, new(0.62f, 0.18f, 0.34f, 0.55f)),
        (ImGuiCol.WindowBg, new(0.11f, 0.045f, 0.07f, 0.97f)),
        (ImGuiCol.ChildBg, new(0.15f, 0.055f, 0.09f, 1f)),
        (ImGuiCol.PopupBg, new(0.13f, 0.05f, 0.08f, 0.98f)),
        (ImGuiCol.Border, new(0.86f, 0.68f, 0.28f, 0.90f)),
        (ImGuiCol.FrameBg, new(0.24f, 0.08f, 0.14f, 1f)),
        (ImGuiCol.FrameBgHovered, new(0.38f, 0.12f, 0.22f, 1f)),
        (ImGuiCol.FrameBgActive, new(0.50f, 0.15f, 0.28f, 1f)),
        (ImGuiCol.TitleBg, new(0.28f, 0.07f, 0.15f, 1f)),
        (ImGuiCol.TitleBgActive, new(0.62f, 0.14f, 0.32f, 1f)),
        (ImGuiCol.TitleBgCollapsed, new(0.22f, 0.06f, 0.12f, 1f)),
        (ImGuiCol.CheckMark, new(0.98f, 0.55f, 0.72f, 1f)),
        (ImGuiCol.SliderGrab, new(0.82f, 0.28f, 0.48f, 1f)),
        (ImGuiCol.SliderGrabActive, new(0.95f, 0.42f, 0.62f, 1f)),
        (ImGuiCol.Button, new(0.46f, 0.12f, 0.26f, 1f)),
        (ImGuiCol.ButtonHovered, new(0.64f, 0.18f, 0.36f, 1f)),
        (ImGuiCol.ButtonActive, new(0.78f, 0.24f, 0.44f, 1f)),
        (ImGuiCol.Header, new(0.42f, 0.11f, 0.24f, 1f)),
        (ImGuiCol.HeaderHovered, new(0.58f, 0.16f, 0.32f, 1f)),
        (ImGuiCol.HeaderActive, new(0.72f, 0.20f, 0.38f, 1f)),
        (ImGuiCol.Separator, new(0.86f, 0.68f, 0.28f, 0.80f)),
        (ImGuiCol.SeparatorHovered, new(0.96f, 0.80f, 0.40f, 0.90f)),
        (ImGuiCol.SeparatorActive, new(1f, 0.88f, 0.50f, 1f)),
        (ImGuiCol.ResizeGrip, new(0.86f, 0.68f, 0.28f, 0.45f)),
        (ImGuiCol.ResizeGripHovered, new(0.96f, 0.80f, 0.40f, 0.75f)),
        (ImGuiCol.ResizeGripActive, new(1f, 0.88f, 0.50f, 0.95f)),
        (ImGuiCol.Tab, new(0.26f, 0.07f, 0.14f, 1f)),
        (ImGuiCol.TabHovered, new(0.68f, 0.18f, 0.36f, 1f)),
        (ImGuiCol.TabActive, new(0.52f, 0.14f, 0.28f, 1f)),
        (ImGuiCol.TabUnfocused, new(0.18f, 0.05f, 0.10f, 1f)),
        (ImGuiCol.TabUnfocusedActive, new(0.36f, 0.10f, 0.20f, 1f)),
        (ImGuiCol.ScrollbarBg, new(0.08f, 0.03f, 0.05f, 1f)),
        (ImGuiCol.ScrollbarGrab, new(0.46f, 0.13f, 0.26f, 1f)),
        (ImGuiCol.ScrollbarGrabHovered, new(0.62f, 0.18f, 0.34f, 1f)),
        (ImGuiCol.ScrollbarGrabActive, new(0.78f, 0.24f, 0.42f, 1f)),
    ];

    public static void Push()
    {
        foreach (var (column, color) in Colors)
            ImGui.PushStyleColor(column, color);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 4f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 4f);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 4f);
        ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, 4f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14f, 12f));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(8f, 5f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 7f));
    }

    public static void Pop()
    {
        ImGui.PopStyleVar(10);
        ImGui.PopStyleColor(Colors.Length);
    }
}
