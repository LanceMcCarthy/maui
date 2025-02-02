using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using ElmSharp;
using Microsoft.Maui.Controls.Internals;
using Microsoft.Maui.Handlers;

[assembly: InternalsVisibleTo("Microsoft.Maui.Controls.Material")]

namespace Microsoft.Maui.Controls.Compatibility.Platform.Tizen
{
	[Obsolete]
	public static class Platform
	{
		internal static readonly BindableProperty RendererProperty = BindableProperty.CreateAttached("Renderer", typeof(IVisualElementRenderer), typeof(Platform), default(IVisualElementRenderer),
			propertyChanged: (bindable, oldvalue, newvalue) =>
			{
				var view = bindable as VisualElement;
				if (view != null)
					view.IsPlatformEnabled = newvalue != null;

				if (bindable is IView mauiView)
				{
					if (mauiView.Handler == null && newvalue is IVisualElementRenderer ver)
						mauiView.Handler = new RendererToHandlerShim(ver);
				}
			});

		public static IVisualElementRenderer GetRenderer(BindableObject bindable)
		{
			return (IVisualElementRenderer)bindable.GetValue(Platform.RendererProperty);
		}

		public static void SetRenderer(BindableObject bindable, IVisualElementRenderer value)
		{
			bindable.SetValue(Platform.RendererProperty, value);
		}

		/// <summary>
		/// Gets the renderer associated with the <c>view</c>. If it doesn't exist, creates a new one.
		/// </summary>
		/// <returns>Renderer associated with the <c>view</c>.</returns>
		/// <param name="element">VisualElement for which the renderer is going to be returned.</param>
		public static IVisualElementRenderer GetOrCreateRenderer(VisualElement element)
		{
			return GetRenderer(element) ?? CreateRenderer(element);
		}

		internal static IVisualElementRenderer CreateRenderer(VisualElement element)
		{
			IVisualElementRenderer renderer = null;

			if (renderer == null)
			{
				IViewHandler handler = null;

				//TODO: Handle this with AppBuilderHost
				try
				{
					handler = Forms.MauiContext.Handlers.GetHandler(element.GetType()) as IViewHandler;
					handler.SetMauiContext(Forms.MauiContext);
				}
				catch
				{
					// TODO define better catch response or define if this is needed?
				}

				if (handler == null)
				{
					renderer = Forms.GetHandlerForObject<IVisualElementRenderer>(element) ?? new DefaultRenderer();
				}
				// This means the only thing registered is the RendererToHandlerShim
				// Which is only used when you are running a .NET MAUI app
				// This indicates that the user hasn't registered a specific handler for this given type
				else if (handler is RendererToHandlerShim shim)
				{
					renderer = shim.VisualElementRenderer;

					if (renderer == null)
					{
						renderer = Forms.GetHandlerForObject<IVisualElementRenderer>(element) ?? new DefaultRenderer();
					}
				}
				else if (handler is IVisualElementRenderer ver)
					renderer = ver;
				else if (handler is IPlatformViewHandler vh)
				{
					if (element.Parent is IView view && view.Handler is IPlatformViewHandler nvh)
					{
						vh.SetParent(nvh);
					}
					renderer = new HandlerToRendererShim(vh);
					element.Handler = handler;
				}
			}
			renderer.SetElement(element);
			return renderer;
		}

		internal static ITizenPlatform CreatePlatform(EvasObject parent)
		{
			if (Forms.PlatformType == PlatformType.Lightweight)
			{
				return new LightweightPlatform(parent);
			}

			return new DefaultPlatform(parent);
		}

		public static SizeRequest GetNativeSize(VisualElement view, double widthConstraint, double heightConstraint)
		{
			widthConstraint = widthConstraint <= -1 ? double.PositiveInfinity : widthConstraint;
			heightConstraint = heightConstraint <= -1 ? double.PositiveInfinity : heightConstraint;

			var renderView = GetRenderer(view);
			if (renderView == null || renderView.NativeView == null)
			{
				return (view is IView iView) ? new SizeRequest(iView.Handler.GetDesiredSize(widthConstraint, heightConstraint)) : new SizeRequest(Graphics.Size.Zero);
			}

			return renderView.GetDesiredSize(widthConstraint, heightConstraint);
		}
	}

	[EditorBrowsable(EditorBrowsableState.Never)]
	public interface ITizenPlatform : IDisposable
	{
		void SetPage(Page page);
		bool SendBackButtonPressed();
		EvasObject GetRootNativeView();
		bool HasAlpha { get; set; }
		event EventHandler<RootNativeViewChangedEventArgs> RootNativeViewChanged;
		bool PageIsChildOfPlatform(Page page);
	}

	public class RootNativeViewChangedEventArgs : EventArgs
	{
		public RootNativeViewChangedEventArgs(EvasObject view) => RootNativeView = view;
		public EvasObject RootNativeView { get; private set; }
	}

	[Obsolete]
	public class DefaultPlatform : BindableObject, ITizenPlatform, INavigation
	{
		NavigationModel _navModel = new NavigationModel();
		bool _disposed;
		readonly Naviframe _internalNaviframe;
		readonly PopupManager _popupManager;

		readonly HashSet<EvasObject> _alerts = new HashSet<EvasObject>();

#pragma warning disable 0067
		public event EventHandler<RootNativeViewChangedEventArgs> RootNativeViewChanged;
#pragma warning restore 0067

		internal DefaultPlatform(EvasObject parent)
		{
			Forms.NativeParent = parent;

			_internalNaviframe = new Naviframe(Forms.NativeParent)
			{
				PreserveContentOnPop = true,
				DefaultBackButtonEnabled = false,
			};
			_internalNaviframe.SetAlignment(-1, -1);
			_internalNaviframe.SetWeight(1.0, 1.0);
			_internalNaviframe.Show();
			_internalNaviframe.AnimationFinished += NaviAnimationFinished;

			if (Forms.UseMessagingCenter)
			{
				_popupManager = new PopupManager(this);
			}
		}

		~DefaultPlatform()
		{
			Dispose(false);
		}

		public Page Page { get; private set; }

		public bool HasAlpha { get; set; }

		Task CurrentModalNavigationTask { get; set; }
		TaskCompletionSource<bool> CurrentTaskCompletionSource { get; set; }
		IPageController CurrentPageController => _navModel.CurrentPage as IPageController;
		IReadOnlyList<Page> INavigation.ModalStack => _navModel.Modals.ToList();
		IReadOnlyList<Page> INavigation.NavigationStack => new List<Page>();

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		public void SetPage(Page newRoot)
		{
			if (Page != null)
			{
				var copyOfStack = new List<NaviItem>(_internalNaviframe.NavigationStack);
				for (var i = 0; i < copyOfStack.Count; i++)
				{
					copyOfStack[i].Delete();
				}
				foreach (Page page in _navModel.Roots)
				{
					var renderer = Platform.GetRenderer(page);
					renderer?.Dispose();
				}
				_navModel = new NavigationModel();
			}

			if (newRoot == null)
				return;

			_navModel.Push(newRoot, null);

			Page = newRoot;

			IVisualElementRenderer pageRenderer = Platform.CreateRenderer(Page);
			var naviItem = _internalNaviframe.Push(pageRenderer.NativeView);
			naviItem.TitleBarVisible = false;

			// Make naviitem transparent if parent window is transparent.
			// Make sure that this is only for _navModel._naviTree. (not for _navModel._modalStack)
			// In addtion, the style of naviItem is only decided before the naviItem pushed into Naviframe. (not on-demand).
			if (HasAlpha)
			{
				naviItem.Style = "default/transparent";
			}

			((Application)Page.RealParent).NavigationProxy.Inner = this;

			Application.Current.Dispatcher.DispatchDelayed(TimeSpan.Zero, () => CurrentPageController?.SendAppearing());
		}

		public bool SendBackButtonPressed()
		{
			bool handled = false;
			if (_navModel.CurrentPage != null)
			{
				if (CurrentModalNavigationTask != null && !CurrentModalNavigationTask.IsCompleted)
				{
					handled = true;
				}
				else
				{
					handled = _navModel.CurrentPage.SendBackButtonPressed();
				}
			}
			return handled;
		}

		public EvasObject GetRootNativeView()
		{
			return _internalNaviframe as EvasObject;
		}

		public bool PageIsChildOfPlatform(Page page)
		{
			var parent = page.AncestorToRoot();
			return Page == parent || _navModel.Roots.Contains(parent);
		}

		protected virtual void Dispose(bool disposing)
		{
			if (_disposed)
				return;
			if (disposing)
			{
				_popupManager?.Dispose();
				SetPage(null);
				_internalNaviframe.Unrealize();
			}
			_disposed = true;
		}

		protected override void OnBindingContextChanged()
		{
			BindableObject.SetInheritedBindingContext(Page, base.BindingContext);
			base.OnBindingContextChanged();
		}

		void INavigation.InsertPageBefore(Page page, Page before)
		{
			throw new InvalidOperationException("InsertPageBefore is not supported globally on Tizen, please use a NavigationPage.");
		}

		Task<Page> INavigation.PopAsync()
		{
			return ((INavigation)this).PopAsync(true);
		}

		Task<Page> INavigation.PopAsync(bool animated)
		{
			throw new InvalidOperationException("PopAsync is not supported globally on Tizen, please use a NavigationPage.");
		}

		Task INavigation.PopToRootAsync()
		{
			return ((INavigation)this).PopToRootAsync(true);
		}

		Task INavigation.PopToRootAsync(bool animated)
		{
			throw new InvalidOperationException("PopToRootAsync is not supported globally on Tizen, please use a NavigationPage.");
		}

		Task INavigation.PushAsync(Page root)
		{
			return ((INavigation)this).PushAsync(root, true);
		}

		Task INavigation.PushAsync(Page root, bool animated)
		{
			throw new InvalidOperationException("PushAsync is not supported globally on Tizen, please use a NavigationPage.");
		}

		void INavigation.RemovePage(Page page)
		{
			throw new InvalidOperationException("RemovePage is not supported globally on Tizen, please use a NavigationPage.");
		}

		Task INavigation.PushModalAsync(Page modal)
		{
			return ((INavigation)this).PushModalAsync(modal, true);
		}

		async Task INavigation.PushModalAsync(Page modal, bool animated)
		{
			var previousPage = CurrentPageController;
			Application.Current.Dispatcher.Dispatch(() => previousPage?.SendDisappearing());

			_navModel.PushModal(modal);

			await PushModalInternal(modal, animated);

			// Verify that the modal is still on the stack
			if (_navModel.CurrentPage == modal)
				CurrentPageController.SendAppearing();
		}

		Task<Page> INavigation.PopModalAsync()
		{
			return ((INavigation)this).PopModalAsync(true);
		}

		async Task<Page> INavigation.PopModalAsync(bool animated)
		{
			Page modal = _navModel.PopModal();

			IVisualElementRenderer modalRenderer = Platform.GetRenderer(modal);
			if (modalRenderer != null)
			{
				await PopModalInternal(animated);
				modalRenderer.Dispose();
			}

			CurrentPageController?.SendAppearing();
			return modal;
		}

		async Task PushModalInternal(Page modal, bool animated)
		{
			TaskCompletionSource<bool> tcs = null;
			if (CurrentModalNavigationTask != null && !CurrentModalNavigationTask.IsCompleted)
			{
				var previousTask = CurrentModalNavigationTask;
				tcs = new TaskCompletionSource<bool>();
				CurrentModalNavigationTask = tcs.Task;
				await previousTask;
			}

			var after = _internalNaviframe.NavigationStack.LastOrDefault();
			NaviItem pushed = null;
			if (animated || after == null)
			{
				pushed = _internalNaviframe.Push(Platform.GetOrCreateRenderer(modal).NativeView, modal.Title);
			}
			else
			{
				pushed = _internalNaviframe.InsertAfter(after, Platform.GetOrCreateRenderer(modal).NativeView, modal.Title);
			}
			pushed.TitleBarVisible = false;

			bool shouldWait = animated && after != null;
			await WaitForCompletion(shouldWait, tcs);
		}

		async Task PopModalInternal(bool animated)
		{
			TaskCompletionSource<bool> tcs = null;
			if (CurrentModalNavigationTask != null && !CurrentModalNavigationTask.IsCompleted)
			{
				var previousTask = CurrentModalNavigationTask;
				tcs = new TaskCompletionSource<bool>();
				CurrentModalNavigationTask = tcs.Task;
				await previousTask;
			}

			if (animated)
			{
				_internalNaviframe.Pop();
			}
			else
			{
				_internalNaviframe.NavigationStack.LastOrDefault()?.Delete();
			}

			bool shouldWait = animated && (_internalNaviframe.NavigationStack.Count != 0);
			await WaitForCompletion(shouldWait, tcs);
		}

		async Task WaitForCompletion(bool shouldWait, TaskCompletionSource<bool> tcs)
		{
			if (shouldWait)
			{
				tcs = tcs ?? new TaskCompletionSource<bool>();
				CurrentTaskCompletionSource = tcs;
				if (CurrentModalNavigationTask == null || CurrentModalNavigationTask.IsCompleted)
				{
					CurrentModalNavigationTask = CurrentTaskCompletionSource.Task;
				}
			}
			else
			{
				tcs?.SetResult(true);
			}

			if (tcs != null)
				await tcs.Task;
		}

		void NaviAnimationFinished(object sender, EventArgs e)
		{
			var tcs = CurrentTaskCompletionSource;
			CurrentTaskCompletionSource = null;
			tcs?.SetResult(true);
		}
	}
}
