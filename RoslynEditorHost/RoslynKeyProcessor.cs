﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;


namespace RoslynEditorHost {

	// The Roslyn commanding system is built around ICommandHandlerService, which intercepts and
	// handles every command.  Unfortunately, the entire system is internal.  I steal the entry-
	// points from AbstractOleCommandTarget and invoke them directly using Reflection. Note that
	// this is rather brittle.
	class RoslynKeyProcessor : KeyProcessor {
		// This delegate matches the signature of the Execute*() methods in AbstractOleCommandTarget
		delegate void CommandExecutor(ITextBuffer subjectBuffer, IContentType contentType, Action executeNextCommandTarget);
		readonly IWpfTextView wpfTextView;
		readonly object innerCommandTarget;

		static readonly Type packageType = Type.GetType("Microsoft.VisualStudio.LanguageServices.CSharp.LanguageService.CSharpPackage, "
													  + "Microsoft.VisualStudio.LanguageServices.CSharp");
		static readonly Type languageServiceType = Type.GetType("Microsoft.VisualStudio.LanguageServices.CSharp.LanguageService.CSharpLanguageService, "
															  + "Microsoft.VisualStudio.LanguageServices.CSharp");
		// The generic parameters aren't actually used, so there is nothing wrong with always using C#.
		// The methods I call are on the non-generic abstract base class anyway.
		static readonly Type oleCommandTargetType = Type.GetType("Microsoft.VisualStudio.LanguageServices.Implementation.StandaloneCommandFilter`3, "
															   + "Microsoft.VisualStudio.LanguageServices")
			.MakeGenericType(packageType, languageServiceType,
				Type.GetType("Microsoft.VisualStudio.LanguageServices.CSharp.ProjectSystemShim.CSharpProject, Microsoft.VisualStudio.LanguageServices.CSharp")
			);

		static readonly Type commandHandlerServiceFactoryType = Type.GetType("Microsoft.CodeAnalysis.Editor.ICommandHandlerServiceFactory, Microsoft.CodeAnalysis.EditorFeatures");

		static readonly MethodInfo mefGetServiceCHSFMethod = typeof(IComponentModel).GetMethod("GetService").MakeGenericMethod(commandHandlerServiceFactoryType);

		public RoslynKeyProcessor(IWpfTextView wpfTextView, IComponentModel mef) {
			this.wpfTextView = wpfTextView;
			innerCommandTarget = CreateInstanceNonPublic(oleCommandTargetType,
				CreateInstanceNonPublic(languageServiceType, Activator.CreateInstance(packageType, true)),	// languageService
				wpfTextView,										// wpfTextView
				mefGetServiceCHSFMethod.Invoke(mef, null),			// commandHandlerServiceFactory
				null,												// featureOptionsService (not used)
				mef.GetService<IVsEditorAdaptersFactoryService>()	// editorAdaptersFactoryService
			);

			AddShortcuts();
		}
		static object CreateInstanceNonPublic(Type type, params object[] args) {
			return Activator.CreateInstance(type, BindingFlags.NonPublic | BindingFlags.Instance, null, args, null);
		}

		readonly Dictionary<Tuple<ModifierKeys, Key>, CommandExecutor> shortcuts = new Dictionary<Tuple<ModifierKeys, Key>, CommandExecutor>();

		protected void AddCommand(Key key, string methodName) {
			AddCommand(ModifierKeys.None, key, methodName);
		}
		protected void AddShiftCommand(Key key, string methodName) {
			AddCommand(ModifierKeys.Shift, key, methodName);
		}
		protected void AddControlCommand(Key key, string methodName) {
			AddCommand(ModifierKeys.Control, key, methodName);
		}
		protected void AddControlShiftCommand(Key key, string methodName) {
			AddCommand(ModifierKeys.Control | ModifierKeys.Shift, key, methodName);
		}
		protected void AddAltShiftCommand(Key key, string methodName) {
			AddCommand(ModifierKeys.Alt | ModifierKeys.Shift, key, methodName);
		}
		protected void AddCommand(ModifierKeys modifiers, Key key, string methodName) {
			var method = (CommandExecutor)Delegate.CreateDelegate(typeof(CommandExecutor), innerCommandTarget, methodName);
			shortcuts.Add(new Tuple<ModifierKeys, Key>(modifiers, key), method);
		}

		public override void KeyDown(KeyEventArgs args) {
			base.KeyDown(args);
			if (args.Handled) return;
			CommandExecutor method;
			if (!shortcuts.TryGetValue(Tuple.Create(args.KeyboardDevice.Modifiers, args.Key), out method))
				return;
			// If an exception is thrown, don't set args.Handled
			var handled = true;
			method(wpfTextView.TextBuffer, wpfTextView.TextBuffer.ContentType, () => handled = false);
			args.Handled = handled;
		}
		#region Shortcuts
		void AddShortcuts() {
			#region Cursor Movement
			AddCommand(Key.Up, "ExecuteUp");
			AddCommand(Key.Down, "ExecuteDown");
			AddCommand(Key.PageUp, "ExecutePageUp");
			AddCommand(Key.PageDown, "ExecutePageDown");

			AddCommand(Key.Home, "ExecuteLineStart");
			AddCommand(Key.End, "ExecuteLineEnd");
			AddShiftCommand(Key.Home, "ExecuteLineStartExtend");
			AddShiftCommand(Key.End, "ExecuteLineEndExtend");

			AddControlCommand(Key.Home, "ExecuteDocumentStart");
			AddControlCommand(Key.End, "ExecuteDocumentEnd");

			AddControlCommand(Key.A, "ExecuteSelectAll");
			#endregion

			AddCommand(Key.F12, "ExecuteGotoDefinition");
			AddCommand(Key.F2, "ExecuteRename");
			AddCommand(Key.Escape, "ExecuteCancel");
			AddControlShiftCommand(Key.Space, "ExecuteParameterInfo");
			AddControlCommand(Key.Space, "ExecuteCommitUniqueCompletionItem");

			AddControlShiftCommand(Key.Down, "ExecutePreviousHighlightedReference");
			AddControlShiftCommand(Key.Up, "ExecuteNextHighlightedReference");

			AddCommand(Key.Back, "ExecuteBackspace");
			AddCommand(Key.Delete, "ExecuteDelete");
			AddControlCommand(Key.Back, "ExecuteWordDeleteToStart");
			AddControlCommand(Key.Delete, "ExecuteWordDeleteToEnd");

			AddCommand(Key.Enter, "ExecuteReturn");
			AddCommand(Key.Tab, "ExecuteTab");
			AddShiftCommand(Key.Tab, "ExecuteBackTab");

			AddControlCommand(Key.V, "ExecutePaste");

			// TODO: These also take an int, which should be 1
			//AddControlCommand(Key.Z, "ExecuteUndo");
			//AddControlCommand(Key.T, "ExecuteRedo");
			//AddControlShiftCommand(Key.Z, "ExecuteRedo");

			// TODO: Invoke peek & light bulbs from IntellisenseCommandFilter
			// TODO: ExecuteTypeCharacter
			// TODO: ExecuteCommentBlock, ExecuteFormatSelection, ExecuteFormatDocument, ExecuteInsertSnippet, ExecuteInsertComment, ExecuteSurroundWith
		}
		#endregion
	}

	[Export(typeof(IKeyProcessorProvider))]
	[TextViewRole(PredefinedTextViewRoles.Interactive)]
	[ContentType("text")]
	[Order(Before = "Standard KeyProcessor")]
	[Name("Roslyn KeyProcessor")]
	sealed class RoslynKeyProcessorProvider : IKeyProcessorProvider {
		[Import]
		public IComponentModel ComponentModel { get; set; }
		public KeyProcessor GetAssociatedProcessor(IWpfTextView wpfTextView) {
			return new RoslynKeyProcessor(wpfTextView, ComponentModel);
		}
	}
}