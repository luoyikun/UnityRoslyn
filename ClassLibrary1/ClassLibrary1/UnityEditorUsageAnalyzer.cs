using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO; // 新增：用于路径处理
using System.Linq;

namespace UnityCodeAnalyzer
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class UnityEditorUsageAnalyzer : DiagnosticAnalyzer
    {
        // --- 核心配置：在这里指定你要监控的文件夹路径 ---
        // 注意：路径是相对于Unity项目根目录的，支持子文件夹递归。
        // 例如: "Assets/Scripts/Gameplay", "Assets/MyModule"
        private static readonly string[] TargetFolders = new string[]
        {
            "Assets/Scripts", // 示例：监控 Assets/Scripts 及其所有子文件夹
            "Assets/MyModule" // 示例：可以添加多个路径
        };
        // --- 配置结束 ---

        // 获取定义好的诊断描述符
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
            => ImmutableArray.Create(DiagnosticDescriptors.UnityEditorInRuntimeCode); // 新增 HeartbeatRule

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeUsingDirective, SyntaxKind.UsingDirective);
        }

        private static void AnalyzeUsingDirective(SyntaxNodeAnalysisContext context)
        {
            var usingDirective = (UsingDirectiveSyntax)context.Node;
            var namespaceName = usingDirective.Name?.ToString();

            if (namespaceName != "UnityEditor" && !namespaceName.StartsWith("UnityEditor."))
            {
                return;
            }

            // 获取文件路径
            var filePath = context.Node.SyntaxTree.FilePath;

            // 1. 如果文件在Editor目录下，允许使用
            if (IsEditorPath(filePath))
            {
                return;
            }

            // +++ 新增的关键步骤 +++
            // 2. 如果文件不在我们指定的目标文件夹内，也跳过分析
            //if (!IsInTargetFolder(filePath))
            //{
            //    return; // 此文件不受本分析器监控
            //}
            // ++++++++++++++++++++++

            // 3. 检查是否被 #if UNITY_EDITOR 预处理指令保护
            if (IsWithinUnityEditorConditionalDirective(usingDirective))
            {
                return;
            }

            // 如果以上条件都不满足，报告诊断
            var diagnostic = Diagnostic.Create(
                DiagnosticDescriptors.UnityEditorInRuntimeCode,
                usingDirective.GetLocation(),
                namespaceName);
            context.ReportDiagnostic(diagnostic);
        }

        private static bool IsEditorPath(string filePath)
        {
            return filePath.IndexOf("/Editor/", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                   filePath.IndexOf("\\Editor\\", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 新增方法：判断文件是否在用户配置的目标文件夹（或其子文件夹）下
        /// </summary>
        private static bool IsInTargetFolder(string filePath)
        {
            // 获取标准化的完整文件路径，确保比较准确
            var fullFilePath = Path.GetFullPath(filePath).Replace('\\', '/');

            foreach (var targetFolder in TargetFolders)
            {
                // 构建目标文件夹的标准化完整路径
                // 注意：这里假设targetFolder是相对于项目根目录的路径。
                // 你需要根据你的分析器获取的filePath的实际情况来调整。
                // 一个常见做法是：如果filePath是绝对路径，我们也要构造一个对应的绝对路径来比较。
                var targetFullPath = Path.GetFullPath(targetFolder).Replace('\\', '/');

                // 检查文件路径是否以目标文件夹路径开头（即位于其内或其子文件夹下）
                if (fullFilePath.Contains(targetFullPath))
                {
                    return true;
                }
            }
            return false; // 不在任何目标文件夹内
        }

        /// <summary>
        /// 增强版：判断 using 指令是否位于任何活动的 #if UNITY_EDITOR（或类似）条件编译块内。
        /// 原理：遍历所有编译指令，模拟一个“条件状态栈”，检查目标位置时的活跃条件。
        /// </summary>
        private static bool IsWithinUnityEditorConditionalDirective(UsingDirectiveSyntax usingDirective)
        {
            var syntaxTree = usingDirective.SyntaxTree;
            var root = syntaxTree.GetRoot();
            var position = usingDirective.SpanStart;

            // 用于记录当前活跃的条件指令的堆栈
            // 每个元素代表一个 #if 或 #elif 指令及其条件文本
            var activeConditionStack = new Stack<(int endPos, string condition)>();

            // 遍历所有 Trivia（包括编译指令）
            foreach (var trivia in root.DescendantTrivia())
            {
                if (!trivia.IsDirective) continue;

                var directive = trivia.GetStructure() as DirectiveTriviaSyntax;
                if (directive == null) continue;

                var directiveSpan = directive.Span;

                // 如果指令在目标位置之后，后面的指令无需再检查
                if (directiveSpan.Start > position) break;

                // 根据指令类型更新“活跃条件栈”
                if (directive is IfDirectiveTriviaSyntax ifDirective)
                {
                    // 遇到 #if，将指令和其条件文本及对应的 #endif 位置（未知，先记一个大的结束范围）入栈
                    // 实际的结束位置会在遇到 #endif 时处理
                    activeConditionStack.Push((int.MaxValue, ifDirective.Condition.ToString()));
                }
                else if (directive is ElifDirectiveTriviaSyntax elifDirective)
                {
                    // 遇到 #elif，替换栈顶的条件（相当于结束了上一个 #if 或 #elif 分支，开启新分支）
                    if (activeConditionStack.Count > 0)
                    {
                        activeConditionStack.Pop();
                        activeConditionStack.Push((int.MaxValue, elifDirective.Condition.ToString()));
                    }
                }
                else if (directive is ElseDirectiveTriviaSyntax)
                {
                    // 遇到 #else，替换栈顶的条件为一个特殊标记（表示进入了无条件为真的 else 块）
                    if (activeConditionStack.Count > 0)
                    {
                        activeConditionStack.Pop();
                        activeConditionStack.Push((int.MaxValue, "ELSE_BLOCK"));
                    }
                }
                else if (directive is EndIfDirectiveTriviaSyntax endifDirective)
                {
                    // 遇到 #endif，将与之配对的 #if/#elif/#else 出栈
                    if (activeConditionStack.Count > 0)
                    {
                        activeConditionStack.Pop();
                    }
                }
            }

            // 遍历结束后，检查在目标位置 `position` 时，栈中活跃的条件是否包含 UNITY_EDITOR
            foreach (var (_, condition) in activeConditionStack)
            {
                // 对于 #else 块，我们认为它继承了上一个 #if 的逆条件。通常 #if UNITY_EDITOR 的 #else 块就是非编辑器代码。
                // 所以如果栈顶是 #else，目标位置在 #else 块内，则不被认为是受 UNITY_EDITOR 保护。
                // 但如果你希望 #else 块内的 using 也被允许，可以在这里特殊处理。
                if (condition.Contains("UNITY_EDITOR"))
                {
                    return true; // 受保护
                }
                // 如果 condition 是 “ELSE_BLOCK”，说明在 #else 内，这里返回 false
            }
            return false; // 不受保护
        }

    }
}

namespace UnityCodeAnalyzer
{
    public static class DiagnosticDescriptors
    {
        public static readonly DiagnosticDescriptor UnityEditorInRuntimeCode =
            new DiagnosticDescriptor(
                id: "UEA001",
                title: "UnityEditor usage in runtime code",
                messageFormat: "禁止在运行时代码中使用 UnityEditor 命名空间 '{0}'。请将该代码移到 Editor 目录下，或使用 #if UNITY_EDITOR 条件编译。",
                category: "Usage",
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                description: "检测到在非编辑器代码中使用了UnityEditor命名空间，这会导致构建失败.");
    }
}