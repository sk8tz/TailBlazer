using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows.Controls;
using System.Windows.Input;
using DynamicData;
using DynamicData.Binding;
using TailBlazer.Domain.Infrastructure;
using TailBlazer.Views;
using TailBlazer.Views.Tail;

namespace TailBlazer.Infrastucture
{
    public enum MouseKeyState
    {
        Down,
        Up
    }

    /// <summary>
    /// Tail Blazer is fast because it uses true data virtualisation. However this causes a huge headache
    /// when trying to copy and paste items which are selected but no longer visible to the clip-board
    /// 
    /// This drawn out and unsophisticated code attempts to deal with that. 
    /// 
    /// BTW: I hear you shout this code should be an abstraction but frankly I cannot be bothered (as this is such a specialisation).
    /// </summary>
    public class SelectionMonitor :  ISelectionMonitor, IAttachedListBox 
    {
        public IObservableList<LineProxy> Selected { get; }

        private readonly ILogger _logger;
        private readonly ISourceList<LineProxy> _selected = new SourceList<LineProxy>();
        private readonly ISourceList<LineProxy> _recentlyRemovedFromVisibleRange = new SourceList<LineProxy>();
        private readonly IDisposable _cleanUp;
        private readonly SerialDisposable _controlSubscriber = new SerialDisposable();
        
        private bool _isSelecting;
        private ListBox _selector;
        private LineProxy _lastSelected = null;

        public SelectionMonitor(ILogger logger)
        {
            _logger = logger;
            Selected = _selected.AsObservableList();

            var selectionLogger = _selected.Connect()
                .ToCollection()
                .Subscribe(collection =>
                {
                    logger.Debug($"{collection.Count} selected: {collection.Select(l=>l.Text).ToDelimited(Environment.NewLine)} ");
                });

            _cleanUp = new CompositeDisposable(
                _selected, 
                _recentlyRemovedFromVisibleRange,
                _controlSubscriber,
                Selected,
                selectionLogger,
                //keep recent items only up to a certain number
                _recentlyRemovedFromVisibleRange.LimitSizeTo(100).Subscribe());
        }

        public string GetSelectedText()
        {
            return GetSelectedItems().ToDelimited(Environment.NewLine);
        }

        public IEnumerable<string> GetSelectedItems()
        {
            return _selected.Items.OrderBy(proxy=> proxy.Index).Select(proxy => proxy.Line.Text);
        } 
        
        void IAttachedListBox.Receive(ListBox selector)
        {
            _selector = selector;

         //   var generator = (ItemContainerGenerator)selector.ItemContainerGenerator;

            _selector.SelectionChanged += _selector_SelectionChanged;

            var dataSource = ((ReadOnlyObservableCollection<LineProxy>) selector.ItemsSource)
                .ToObservableChangeSet()
                .Publish();

            //Re-select any selected items which are scrolled back into view
            var itemsAdded = dataSource.WhereReasonsAre(ListChangeReason.Add,ListChangeReason.AddRange)
                .Subscribe(changes =>
                {
                    var alreadySelected = _selected.Items.ToArray();
                    var newItems = changes.Flatten().Select(c=>c.Current).ToArray();

                    foreach (var item in newItems)
                    {
                        if (alreadySelected.Contains(item) && !_selector.SelectedItems.Contains(item))
                            _selector.SelectedItems.Add(item);
                    }
                });

            //monitor items which have moved off screen [store these for off screen multi-select]
            var itemsRemoved = dataSource.WhereReasonsAre(ListChangeReason.Remove, ListChangeReason.RemoveRange,ListChangeReason.Clear)
                .Subscribe(changes =>
                {
                    var oldItems = changes.Flatten().Select(c => c.Current).ToArray();
                    //edit ensures items are added in 1 batch
                    _recentlyRemovedFromVisibleRange.Edit(innerList =>
                    {
                        foreach (var item in oldItems.Where(item => !innerList.Contains(item)))
                        {
                            innerList.Add(item);
                        }
                    });

                });

            //clear selection when the mouse is clicked and no other key is pressed
            var mouseDownHandler = Observable.FromEventPattern<MouseButtonEventHandler, MouseButtonEventArgs>(
                                    h => selector.PreviewMouseLeftButtonDown += h,
                                    h => selector.PreviewMouseLeftButtonDown -= h)
                                    .Select(evt => evt.EventArgs)
                                    .Subscribe(mouseArgs =>
                                    {
                                        var isKeyDown = (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)
                                         || Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightCtrl));

                                        if (!isKeyDown) _selected.Clear();
                                    });

            var mouseUpHandler = Observable.FromEventPattern<MouseButtonEventHandler, MouseButtonEventArgs>(
                        h => selector.PreviewMouseLeftButtonUp += h,
                        h => selector.PreviewMouseLeftButtonUp -= h)
                        .Select(x => MouseKeyState.Up);


            var selectedChanged = Observable
                .FromEventPattern<SelectionChangedEventHandler, SelectionChangedEventArgs>(
                    h => selector.SelectionChanged += h,
                    h => selector.SelectionChanged -= h)
                .Select(evt => evt.EventArgs);


            var mouseDown = Observable.FromEventPattern<MouseButtonEventHandler, MouseButtonEventArgs>(
                                        h => selector.PreviewMouseLeftButtonDown += h,
                                        h => selector.PreviewMouseLeftButtonDown -= h)
                                        .Select(evt => evt.EventArgs);

            //Handle selecting multiple rows with the mouse
            // TODO: Scroll up when the mouse it at the top of the screen
            var mouseDragSelector =  selectedChanged.CombineLatest(mouseDown, (slct,down) =>new  { slct, down })
                    
                   
                    .Scan(new ImmutableList<LineProxy>(), (state, latest) =>
                    {
                        return state.Add(latest.slct.AddedItems.OfType<LineProxy>().ToList());
                    }).Select(list => list.Data.Distinct().ToArray())
                    .TakeUntil(mouseUpHandler)
                    .Repeat()
                    .Where(selection=>selection.Length>0)
                    .Subscribe(selection =>
                    {
                        var first = selection.OrderBy(proxy => proxy.Start).First();
                        var last = selection.OrderBy(proxy => proxy.Start).Last();

                        var fromCurrentPage = _selector.Items.OfType<LineProxy>()
                            .Where(lp => lp.Start >= first.Start && lp.Start <= last.Start)
                            .ToArray();

                        foreach (var item in fromCurrentPage)
                            _selector.SelectedItems.Add(item);
      
                        _logger.Debug($"{selection.Length} selected. Page={fromCurrentPage.Length}" );
                    });


            var selectionChanged = selectedChanged.Subscribe(OnSelectedItemsChanged);

            _controlSubscriber.Disposable =new CompositeDisposable(mouseDownHandler, mouseDragSelector, selectionChanged, itemsAdded, itemsRemoved, dataSource.Connect());
        }

        private void _selector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
          
        }

        private void OnSelectedItemsChanged(SelectionChangedEventArgs args)
        {

            //Logic - by default when items scroll out of view they are no longer selected.
            //this is because the panel is virtualised and and automatically unselected due
            //to the control thinking that the item is not longer part of the overall collection
            if (_isSelecting) return;
            try
            {
                _isSelecting = true;

                _selected.Edit(innerList =>
                {
                    var toAdd = args.AddedItems.OfType<LineProxy>().ToList();

                    //may need to track if last selected is off the page:
                    var isShiftKeyDown = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);

                    //get the last item at then end of the list

                    if (!isShiftKeyDown)
                    {
                        //if mouse down, we need to prevent items being cleated

                        //add items to list
                        foreach (var lineProxy in toAdd)
                        {
                            if (innerList.Contains(lineProxy)) continue;
                            _lastSelected = lineProxy;
                            innerList.Add(lineProxy);
                        }
                    }
                    
                    else
                    {
                        if (_lastSelected == null)
                        {

                            foreach (var lineProxy in toAdd)
                            {
                                if (innerList.Contains(lineProxy)) continue;
                                _lastSelected = lineProxy;
                                innerList.Add(lineProxy);
                            }
                            args.Handled = true;
                            return;
                        }

                        //if shift down we need to override selected and manually select our selves
                        var last = _lastSelected.Index;


                        var allSelectedItems = _selector.SelectedItems.OfType<LineProxy>().ToArray();
                        var currentPage = _selector.Items.OfType<LineProxy>().ToArray();

                        //1. Determine whether all selected items are on the current page [plus whether last is on the current page]
                        var allOnCurrentPage = allSelectedItems.Intersect(currentPage).ToArray();

                        var lastInONcurrentPage = currentPage.Contains(_lastSelected);
                        if (lastInONcurrentPage && allOnCurrentPage.Length == allSelectedItems.Length)
                        {
                            innerList.Clear();
                            innerList.AddRange(allSelectedItems);
                            return;
                        }

                        args.Handled = true;
                        var maxOfRecent = toAdd.Max(lp => lp.Index);



                        int min;
                        int max;
                        if (last < maxOfRecent)
                        {
                            min = last;
                            max = maxOfRecent;
                        }
                        else
                        {
                            min = maxOfRecent;
                            max = last;
                        }

                        //maintain selection
                        _selector.SelectedItems.Clear();
                        var fromCurrentPage = _selector.Items.OfType<LineProxy>()
                            .Where(lp => lp.Index >= min && lp.Index <= max)
                            .ToArray();

                        var fromPrevious = _recentlyRemovedFromVisibleRange
                            .Items.Where(lp => lp.Index >= min && lp.Index <= max)
                            .ToArray();

                        _recentlyRemovedFromVisibleRange.Clear();

                        //maintain our record
                        innerList.Clear();
                        innerList.AddRange(fromCurrentPage);
                        foreach (var previous in fromPrevious)
                        {
                            if (!innerList.Contains(previous))
                                innerList.Add(previous);
                        }

                        //finally reload the actual selection:
                        foreach (var item in innerList)
                        {
                            _selector.SelectedItems.Add(item);
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.Error(ex,"There has been a problem with manual selection");
            }
            finally
            {
                _isSelecting = false;
            }
        }

        public void Dispose()
        {
            _cleanUp.Dispose();
        }
    }

}
