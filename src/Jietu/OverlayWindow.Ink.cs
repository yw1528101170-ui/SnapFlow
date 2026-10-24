using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;

namespace Jietu;

public sealed partial class OverlayWindow
{
    private void OnUndo(object sender, RoutedEventArgs e)
    {
        // 拖拽画到一半按 Ctrl+Z：撤销的是「当前这笔还没提交的笔迹」，不动已提交的撤销/重做栈。
        // 旧实现会弹掉上一笔并清空 redo，抬手后当前笔又照常落盘，与主流工具语义相反。
        if (DiscardLiveStroke())
        {
            return;
        }

        CloseText(false);
        if (_strokes.Count == 0)
        {
            return;
        }

        _redo.Add(_strokes[^1]);
        _strokes.RemoveAt(_strokes.Count - 1);
        RebuildInk();
    }

    private void OnRedo(object sender, RoutedEventArgs e)
    {
        // 拖拽中忽略重做：此时撤销语义是「丢弃当前笔迹」，重做栈不应被消费。
        if (_dragging && _locked && _tool != null)
        {
            return;
        }

        CloseText(false);
        if (_redo.Count == 0)
        {
            return;
        }

        _strokes.Add(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
        RebuildInk();
    }

    /// <summary>
    /// 丢弃正在绘制、尚未提交的笔迹（草稿点 + 临时墨迹 + 实时马赛克图层）。
    /// 返回 false 表示当前没有可丢弃的笔迹（未在绘图，或处于新建选区拖拽中）。
    /// </summary>
    private bool DiscardLiveStroke()
    {
        if (!_dragging || !_locked || _tool == null)
        {
            return false;
        }

        _dragging = false;
        _draft.Clear();
        TempInk.Children.Clear();
        ResetLiveMosaic();
        _mosaicTruncated = false;
        Root.ReleasePointerCaptures();
        SetHint("已取消当前笔迹");
        return true;
    }

    private void OnStrokeWidth(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || !int.TryParse(tag, out var dip))
        {
            return;
        }

        _strokeDip = dip;
        PaintWidths();
    }

    private void PaintWidths()
    {
        PaintWidth(ThinButton, 2);
        PaintWidth(MidButton, 3);
        PaintWidth(ThickButton, 6);
    }

    private void PaintWidth(Button button, int dip)
    {
        if (button.Content is not Ellipse dot)
        {
            return;
        }

        var on = _strokeDip == dip;
        dot.Fill = new SolidColorBrush(on ? EmphasisColor : MutedColor);
        button.Background = new SolidColorBrush(on ? SelectionChipColor : Microsoft.UI.Colors.Transparent);
    }

    private async void OnSaveAs(object sender, RoutedEventArgs e)
    {
        CloseText(true);
        if (_current == null)
        {
            return;
        }

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = false;
        }

        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                SuggestedFileName = "SnapFlow-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
            };
            picker.FileTypeChoices.Add("PNG 图片", [".png"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file == null || BuildShot() is not PickedShot shot)
            {
                return;
            }

            var png = await ImageBytes.ToPngAsync(shot.Pixels, shot.Width, shot.Height);
            await File.WriteAllBytesAsync(file.Path, png);
            SetHint("已保存到 " + file.Name);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            SetHint("保存失败");
        }
        finally
        {
            if (AppWindow.Presenter is OverlappedPresenter top)
            {
                top.IsAlwaysOnTop = true;
            }

            Activate();
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Complete(null);

    private void OnConfirm(object sender, RoutedEventArgs e) => Confirm();

    private void Confirm()
    {
        if (BuildShot() is not PickedShot shot)
        {
            Complete(null);
            return;
        }

        Complete(shot);
    }

    private PickedShot? BuildShot()
    {
        CloseText(true);
        var pin = _pinRequested;
        _pinRequested = false;
        var extract = _extractText;
        var extractCode = _extractCode;
        var longCapture = _longCapture && !extract && !extractCode;
        var translate = _translateRequested;
        var share = _shareRequested;
        var aiVision = _aiVisionRequested;
        _extractText = false;
        _extractCode = false;
        _longCapture = false;
        _translateRequested = false;
        _shareRequested = false;
        _aiVisionRequested = false;
        if (_current is not PixelRect rect)
        {
            return null;
        }

        var cropped = Crop(_source, _frameWidth, rect);
        var local = _strokes.Select(stroke =>
        {
            var points = stroke.Points.Select(point => (point.X - rect.X, point.Y - rect.Y)).ToList();
            if (stroke.Kind == AnnotateKind.Spotlight && points.Count >= 2)
            {
                for (var i = 0; i < points.Count; i++)
                {
                    points[i] = (
                        Math.Clamp(points[i].Item1, 0, rect.Width),
                        Math.Clamp(points[i].Item2, 0, rect.Height)
                    );
                }
            }

            return new AnnotateStroke
        {
            Kind = stroke.Kind,
            B = stroke.B,
            G = stroke.G,
            R = stroke.R,
            Text = stroke.Text,
            FontPx = stroke.FontPx,
            WidthPx = stroke.WidthPx,
            Points = points,
        };
    }).ToList();
        return new PickedShot
        {
            Width = rect.Width,
            Height = rect.Height,
            Pixels = AnnotationRenderer.Render(cropped, rect.Width, rect.Height, local),
            Pin = pin && !extract && !extractCode && !longCapture && !translate && !aiVision,
            ExtractText = extract,
            ExtractCode = extractCode,
            LongCapture = longCapture,
            Translate = translate,
            Share = share,
            AiVision = aiVision,
            ScreenX = _monitor.X + rect.X,
            ScreenY = _monitor.Y + rect.Y,
        };
    }

    private void OnAiVision(object sender, RoutedEventArgs e)
    {
        _aiVisionRequested = true;
        Confirm();
    }

    private async void OnSmartRedact(object sender, RoutedEventArgs e)
    {
        if (_current is not PixelRect rect || rect.Width < 4 || rect.Height < 4)
        {
            return;
        }

        CloseText(false);
        SetHint("正在智能扫描并识别敏感隐私信息 (手机 / 身份证 / 邮箱 / IP / 密钥)…");

        try
        {
            var cropped = Crop(_source, _frameWidth, rect);
            var png = await ImageBytes.ToPngAsync(cropped, rect.Width, rect.Height);
            var boxes = await PrivacyRedactService.DetectAsync(png);

            if (boxes.Count == 0)
            {
                SetHint("智能脱敏：未在选区中发现敏感隐私信息");
                return;
            }

            foreach (var b in boxes)
            {
                var p1 = (rect.X + b.X, rect.Y + b.Y);
                var p2 = (rect.X + b.X + b.Width, rect.Y + b.Y + b.Height);
                RememberStroke(new AnnotateStroke
                {
                    Kind = AnnotateKind.Mosaic,
                    B = _blue,
                    G = _green,
                    R = _red,
                    Points = [p1, p2],
                    Text = "full",
                    WidthPx = 10,
                });
            }

            RebuildInk();
            SetHint($"智能脱敏：已自动遮蔽 {boxes.Count} 处敏感信息（按 Ctrl+Z 可撤销）");
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            SetHint("智能脱敏识别异常: " + ex.Message);
        }
    }

    private void OnTranslate(object sender, RoutedEventArgs e)
    {
        _translateRequested = true;
        Confirm();
    }

    private void OnShare(object sender, RoutedEventArgs e)
    {
        _shareRequested = true;
        Confirm();
    }

        private void OnExtractText(object sender, RoutedEventArgs e)
    {
        _extractText = true;
        Confirm();
    }

    private void OnExtractCode(object sender, RoutedEventArgs e)
    {
        _extractCode = true;
        Confirm();
    }

    private void OnLong(object sender, RoutedEventArgs e)
    {
        _longCapture = true;
        Confirm();
    }

    private void OnFontSize(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || !int.TryParse(tag, out var dip))
        {
            return;
        }

        _fontDip = dip;
        PaintFonts();
        if (_textInput != null)
        {
            _textInput.FontSize = dip;
        }
    }

    private void PaintFonts()
    {
        PaintFont(SmallFontButton, 14);
        PaintFont(MidFontButton, 20);
        PaintFont(LargeFontButton, 28);
    }

    private void PaintFont(Button button, int dip)
    {
        var on = _fontDip == dip;
        button.Background = new SolidColorBrush(on ? SelectionChipColor : Microsoft.UI.Colors.Transparent);
        button.Foreground = new SolidColorBrush(on ? EmphasisColor : MutedColor);
        if (button.Content is TextBlock label)
        {
            label.Foreground = button.Foreground;
        }
    }

    private void PaintHistory()
    {
        UndoButton.IsEnabled = _strokes.Count > 0;
        if (RedoButton != null)
        {
            RedoButton.IsEnabled = _redo.Count > 0;
        }
    }

    private void PlaceEmoji(string emoji, (int X, int Y) pixel)
    {
        RememberStroke(Stroke(AnnotateKind.Emoji, [pixel], emoji));
        RebuildInk();
    }

    private void PlaceNumber((int X, int Y) pixel)
    {
        var next = 1;
        foreach (var stroke in _strokes)
        {
            if (stroke.Kind == AnnotateKind.Number && int.TryParse(stroke.Text, out var value))
            {
                next = Math.Max(next, value + 1);
            }
        }

        RememberStroke(Stroke(AnnotateKind.Number, [pixel], next.ToString()));
        RebuildInk();
    }

    private void OnPin(object sender, RoutedEventArgs e)
    {
        _pinRequested = true;
        Confirm();
    }

    private static byte[] Crop(byte[] source, int width, PixelRect rect)
    {
        var cropped = new byte[rect.Width * rect.Height * 4];
        if (rect.X == 0 && rect.Width == width)
        {
            System.Buffer.BlockCopy(source, rect.Y * width * 4, cropped, 0, cropped.Length);
            return cropped;
        }

        var rowBytes = rect.Width * 4;
        var srcStride = width * 4;
        var srcOffset = (rect.Y * width + rect.X) * 4;
        var dstOffset = 0;
        for (var row = 0; row < rect.Height; row++)
        {
            System.Buffer.BlockCopy(source, srcOffset, cropped, dstOffset, rowBytes);
            srcOffset += srcStride;
            dstOffset += rowBytes;
        }

        return cropped;
    }

    private void RebuildInk()
    {
        ResetLiveMosaic();
        Ink.Children.Clear();
        var spotlights = _strokes.Where(s => s.Kind == AnnotateKind.Spotlight && s.Points.Count >= 2).ToList();
        if (spotlights.Count > 0 && _current is PixelRect currentRect)
        {
            DrawSpotlights(Ink, spotlights, currentRect);
        }
        foreach (var stroke in _strokes)
        {
            var brush = new SolidColorBrush(Color.FromArgb(255, stroke.R, stroke.G, stroke.B));
            switch (stroke.Kind)
            {
                case AnnotateKind.Pen:
                    var pen = new Polyline
                    {
                        Stroke = brush,
                        StrokeThickness = DipWidth(stroke),
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                    };
                    foreach (var point in stroke.Points)
                    {
                        pen.Points.Add(DipFromPixel(point.X, point.Y));
                    }

                    Ink.Children.Add(pen);
                    break;
                case AnnotateKind.Highlighter:
                    var hlBrush = new SolidColorBrush(Color.FromArgb(120, stroke.R, stroke.G, stroke.B));
                    var hl = new Polyline
                    {
                        Stroke = hlBrush,
                        StrokeThickness = stroke.WidthPx > 0 ? Math.Max(8, stroke.WidthPx * 4 / Scale) : _strokeDip * 4,
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                    };
                    foreach (var point in stroke.Points)
                    {
                        hl.Points.Add(DipFromPixel(point.X, point.Y));
                    }

                    Ink.Children.Add(hl);
                    break;
                case AnnotateKind.Text:
                    var block = new TextBlock
                    {
                        Text = stroke.Text,
                        Foreground = brush,
                        FontSize = stroke.FontPx > 0 ? Math.Max(12, stroke.FontPx / Scale) : _fontDip,
                        TextWrapping = TextWrapping.NoWrap,
                        IsHitTestVisible = false,
                    };
                    var at = DipFromPixel(stroke.Points[0].X, stroke.Points[0].Y);
                    // 与导出一致：导出是「先把画面裁到选区、再画字」，超出选区的部分会被裁掉。
                    // 这里不在 TextBlock 上单独设 Clip（选区一改就会过期），而是由 ShowSelection
                    // 给整个 Ink 画布设置「选区裁剪」（见 OverlayWindow.xaml.cs ApplyInkClip），
                    // 移动/缩放选区时裁剪会跟着更新，长文本与贴边文字都不会再「预览完整、导出半截」。
                    Canvas.SetLeft(block, at.X);
                    Canvas.SetTop(block, at.Y);
                    Ink.Children.Add(block);
                    break;
                case AnnotateKind.Number:
                    // 与导出共用同一套尺寸公式（AnnotationRenderer.NumberBadgeMetrics），
                    // 100% DPI 下预览圆直径/字号与导出像素完全一致。
                    var badge = AnnotationRenderer.NumberBadgeMetrics(stroke.FontPx);
                    var badgeDiameter = Math.Max(12, badge.Radius * 2) / Scale;
                    var host = new Grid
                    {
                        Width = badgeDiameter,
                        Height = badgeDiameter,
                        IsHitTestVisible = false,
                    };
                    host.Children.Add(new Ellipse { Fill = brush });
                    var light = (stroke.R * 3) + (stroke.G * 6) + stroke.B > 1600;
                    host.Children.Add(new TextBlock
                    {
                        Text = stroke.Text,
                        Foreground = new SolidColorBrush(light ? Color.FromArgb(255, 17, 17, 17) : Color.FromArgb(255, 255, 255, 255)),
                        FontSize = Math.Max(1, badge.FontSize / Scale),
                        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                    var center = DipFromPixel(stroke.Points[0].X, stroke.Points[0].Y);
                    Canvas.SetLeft(host, center.X - (badgeDiameter / 2));
                    Canvas.SetTop(host, center.Y - (badgeDiameter / 2));
                    Ink.Children.Add(host);
                    break;
                case AnnotateKind.Emoji:
                    var emojiSize = Math.Max(28, stroke.FontPx > 0 ? stroke.FontPx / Scale : 32);
                    var emojiBlock = new TextBlock
                    {
                        Text = stroke.Text,
                        FontSize = emojiSize,
                        FontFamily = new FontFamily("Segoe UI Emoji"),
                        IsHitTestVisible = false,
                    };
                    var emojiPt = DipFromPixel(stroke.Points[0].X, stroke.Points[0].Y);
                    Canvas.SetLeft(emojiBlock, emojiPt.X - (emojiSize / 2));
                    Canvas.SetTop(emojiBlock, emojiPt.Y - (emojiSize / 2));
                    Ink.Children.Add(emojiBlock);
                    break;
                case AnnotateKind.Ellipse when stroke.Points.Count >= 2:
                    AddShape(new Ellipse(), stroke);
                    break;
                case AnnotateKind.Arrow when stroke.Points.Count >= 2:
                    AddShapeArrow(stroke);
                    break;
                case AnnotateKind.Rectangle when stroke.Points.Count >= 2:
                    AddShape(new Rectangle(), stroke);
                    break;
                case AnnotateKind.Mosaic when stroke.Text == "full" && stroke.Points.Count >= 2:
                    DrawMosaicRect(Ink, stroke.Points[0], stroke.Points[^1], stroke.WidthPx > 0 ? stroke.WidthPx : 16);
                    break;
                case AnnotateKind.Mosaic when stroke.Points.Count >= 1:
                    DrawMosaicBrush(Ink, stroke.Points, stroke.WidthPx > 0 ? stroke.WidthPx : 18);
                    break;
            }
        }

        PaintHistory();
    }

    private void AddShape(Shape shape, AnnotateStroke stroke)
    {
        var start = DipFromPixel(stroke.Points[0].X, stroke.Points[0].Y);
        var end = DipFromPixel(stroke.Points[^1].X, stroke.Points[^1].Y);
        shape.Width = Math.Max(1, Math.Abs(end.X - start.X));
        shape.Height = Math.Max(1, Math.Abs(end.Y - start.Y));
        shape.Stroke = new SolidColorBrush(Color.FromArgb(255, stroke.R, stroke.G, stroke.B));
        shape.StrokeThickness = DipWidth(stroke);
        shape.IsHitTestVisible = false;
        Ink.Children.Add(shape);
        Canvas.SetLeft(shape, Math.Min(start.X, end.X));
        Canvas.SetTop(shape, Math.Min(start.Y, end.Y));
    }

    private void AddShapeArrow(AnnotateStroke stroke)
    {
        var brush = new SolidColorBrush(Color.FromArgb(255, stroke.R, stroke.G, stroke.B));
        var start = DipFromPixel(stroke.Points[0].X, stroke.Points[0].Y);
        var end = DipFromPixel(stroke.Points[^1].X, stroke.Points[^1].Y);
        AddLiveArrow(start, end, brush, DipWidth(stroke));
        foreach (var child in TempInk.Children.ToList())
        {
            TempInk.Children.Remove(child);
            Ink.Children.Add(child);
        }
    }

    private void DrawTemp(Point start, Point end)
    {
        FitCanvas(TempInk);
        FitCanvas(Ink);
        if (_tool == AnnotateKind.Mosaic)
        {
            // 马赛克自己维护一张增量位图（见 MosaicLayer），不能跟着其它工具一起 Clear 重建
            var live = new List<(int X, int Y)>(_draft);
            var tip = ToPixel(end);
            if (live.Count == 0 || live[^1] != tip)
            {
                live.Add(tip);
            }

            DrawLiveMosaic(live, BrushRadius());
            return;
        }

        ResetLiveMosaic();
        TempInk.Children.Clear();
        var brush = new SolidColorBrush(Color.FromArgb(255, _red, _green, _blue));
        switch (_tool)
        {
            case AnnotateKind.Pen:
                if (ShiftDown())
                {
                    TempInk.Children.Add(new Line
                    {
                        X1 = start.X,
                        Y1 = start.Y,
                        X2 = end.X,
                        Y2 = end.Y,
                        Stroke = brush,
                        StrokeThickness = _strokeDip,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                    });
                    break;
                }

                var pen = new Polyline
                {
                    Stroke = brush,
                    StrokeThickness = _strokeDip,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                };
                foreach (var point in _draft)
                {
                    pen.Points.Add(DipFromPixel(point.X, point.Y));
                }

                pen.Points.Add(end);
                TempInk.Children.Add(pen);
                break;
            case AnnotateKind.Highlighter:
                var hlBrush = new SolidColorBrush(Color.FromArgb(120, _red, _green, _blue));
                var hlThick = _strokeDip * 4;
                if (ShiftDown())
                {
                    TempInk.Children.Add(new Line
                    {
                        X1 = start.X,
                        Y1 = start.Y,
                        X2 = end.X,
                        Y2 = end.Y,
                        Stroke = hlBrush,
                        StrokeThickness = hlThick,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                    });
                    break;
                }

                var hlPoly = new Polyline
                {
                    Stroke = hlBrush,
                    StrokeThickness = hlThick,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                };
                foreach (var point in _draft)
                {
                    hlPoly.Points.Add(DipFromPixel(point.X, point.Y));
                }

                hlPoly.Points.Add(end);
                TempInk.Children.Add(hlPoly);
                break;
            case AnnotateKind.Spotlight:
                if (_current is PixelRect liveRect)
                {
                    var spotlightList = new List<AnnotateStroke>
                    {
                        new()
                        {
                            Kind = AnnotateKind.Spotlight,
                            B = 0, G = 0, R = 0,
                            Points = [ToPixel(start), ToPixel(end)],
                        }
                    };
                    DrawSpotlights(TempInk, spotlightList, liveRect);
                }
                break;
            case AnnotateKind.Ellipse:
                AddLiveBox(new Ellipse(), start, end, brush, fill: false);
                break;
            case AnnotateKind.Mosaic:
                // 已在函数开头单独处理（增量位图）
                break;
            case AnnotateKind.Arrow:
                AddLiveArrow(start, end, brush, _strokeDip);
                break;
            default:
                AddLiveBox(new Rectangle(), start, end, brush, fill: false);
                break;
        }
    }

    private void DrawSpotlights(Canvas target, IReadOnlyList<AnnotateStroke> spotlights, PixelRect sel)
    {
        var selLeft = DipX(sel.X);
        var selTop = DipY(sel.Y);
        var selRight = DipX(sel.X + sel.Width);
        var selBottom = DipY(sel.Y + sel.Height);

        var pathGeometry = new PathGeometry { FillRule = FillRule.EvenOdd };
        var outer = new PathFigure { StartPoint = new Point(selLeft, selTop), IsClosed = true };
        outer.Segments.Add(new LineSegment { Point = new Point(selRight, selTop) });
        outer.Segments.Add(new LineSegment { Point = new Point(selRight, selBottom) });
        outer.Segments.Add(new LineSegment { Point = new Point(selLeft, selBottom) });
        pathGeometry.Figures.Add(outer);

        foreach (var s in spotlights)
        {
            var p0 = DipFromPixel(s.Points[0].X, s.Points[0].Y);
            var p1 = DipFromPixel(s.Points[^1].X, s.Points[^1].Y);
            var left = Math.Clamp(Math.Min(p0.X, p1.X), selLeft, selRight);
            var top = Math.Clamp(Math.Min(p0.Y, p1.Y), selTop, selBottom);
            var right = Math.Clamp(Math.Max(p0.X, p1.X), selLeft, selRight);
            var bottom = Math.Clamp(Math.Max(p0.Y, p1.Y), selTop, selBottom);
            if (right - left < 1 || bottom - top < 1)
            {
                continue;
            }

            var inner = new PathFigure { StartPoint = new Point(left, top), IsClosed = true };
            inner.Segments.Add(new LineSegment { Point = new Point(right, top) });
            inner.Segments.Add(new LineSegment { Point = new Point(right, bottom) });
            inner.Segments.Add(new LineSegment { Point = new Point(left, bottom) });
            pathGeometry.Figures.Add(inner);

            var border = new Rectangle
            {
                Width = Math.Max(1, right - left),
                Height = Math.Max(1, bottom - top),
                Stroke = new SolidColorBrush(Color.FromArgb(240, 255, 255, 255)),
                StrokeThickness = 2,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(border, left);
            Canvas.SetTop(border, top);
            target.Children.Add(border);
        }

        var dimPath = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = pathGeometry,
            Fill = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
            IsHitTestVisible = false,
        };
        target.Children.Insert(0, dimPath);
    }

    private void AddLiveBox(Shape shape, Point start, Point end, SolidColorBrush brush, bool fill)
    {
        shape.Width = Math.Max(1, Math.Abs(end.X - start.X));
        shape.Height = Math.Max(1, Math.Abs(end.Y - start.Y));
        shape.Stroke = brush;
        shape.StrokeThickness = _strokeDip;
        shape.IsHitTestVisible = false;
        if (fill)
        {
            shape.Fill = new SolidColorBrush(Color.FromArgb(80, _red, _green, _blue));
        }

        TempInk.Children.Add(shape);
        Canvas.SetLeft(shape, Math.Min(start.X, end.X));
        Canvas.SetTop(shape, Math.Min(start.Y, end.Y));
    }

    private void AddLiveArrow(Point start, Point end, SolidColorBrush brush, double thickness)
    {
        var angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
        var distance = Math.Sqrt(((end.X - start.X) * (end.X - start.X)) + ((end.Y - start.Y) * (end.Y - start.Y)));
        var scale = Math.Max(0.6, thickness / 3);
        var back = Math.Min(12 * scale, distance * 0.4);
        var neck = new Point(end.X - Math.Cos(angle) * back, end.Y - Math.Sin(angle) * back);
        TempInk.Children.Add(new Line
        {
            X1 = start.X,
            Y1 = start.Y,
            X2 = neck.X,
            Y2 = neck.Y,
            Stroke = brush,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        });
        var length = 16 * scale;
        var spread = Math.PI / 6.5;
        TempInk.Children.Add(new Polygon
        {
            Fill = brush,
            IsHitTestVisible = false,
            Points = new PointCollection
            {
                end,
                new Point(end.X - Math.Cos(angle - spread) * length, end.Y - Math.Sin(angle - spread) * length),
                new Point(end.X - Math.Cos(angle + spread) * length, end.Y - Math.Sin(angle + spread) * length),
            },
        });
    }

    private int BrushRadius() => Math.Max(10, (int)Math.Round(_strokeDip * 6 * Math.Max(1, Scale)));

    private void OnOneClickMosaic(object sender, RoutedEventArgs e)
    {
        if (_current is not PixelRect rect || rect.Width < 2 || rect.Height < 2)
        {
            return;
        }

        CloseText(false);
        var block = _strokeDip switch
        {
            <= 2 => 10,
            >= 6 => 24,
            _ => 16,
        };
        RememberStroke(new AnnotateStroke
        {
            Kind = AnnotateKind.Mosaic,
            B = _blue,
            G = _green,
            R = _red,
            Points = [(rect.X, rect.Y), (rect.X + rect.Width - 1, rect.Y + rect.Height - 1)],
            Text = "full",
            WidthPx = block,
        });
        RebuildInk();
    }

    private void DrawMosaicRect(Canvas canvas, (int X, int Y) start, (int X, int Y) end, int block)
    {
        var image = MosaicImage(AnnotationRenderer.MosaicBlocks(_source, _frameWidth, _frameHeight, start, end, Math.Max(8, block)));
        if (image != null)
        {
            canvas.Children.Add(image);
        }
    }

    private void DrawMosaicBrush(Canvas canvas, IReadOnlyList<(int X, int Y)> points, int radius)
    {
        var tiles = AnnotationRenderer.MosaicBrush(_source, _frameWidth, _frameHeight, points, radius);
        var image = MosaicImage(tiles);
        if (image != null)
        {
            canvas.Children.Add(image);
        }
    }

    /// <summary>
    /// 把一批瓦片压成一张位图 + 一个 Image。
    /// 原来每个瓦片都是一个 Rectangle（一次整框马赛克能到上万个元素，长笔刷上限 2400 个），
    /// 每次重绘都要重新创建这么多 XAML 元素和 Brush，UI 线程会被直接压死。
    /// </summary>
    private Image? MosaicImage(IReadOnlyList<AnnotationRenderer.MosaicBlock> tiles)
    {
        var bounds = MosaicRaster.Bounds(tiles);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return null;
        }

        var pixels = new byte[bounds.Width * bounds.Height * 4];
        MosaicRaster.Paint(pixels, bounds.Width, bounds.Height, bounds.X, bounds.Y, tiles);

        var bitmap = new WriteableBitmap(bounds.Width, bounds.Height);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(pixels, 0, pixels.Length);
        }

        bitmap.Invalidate();
        return PlaceMosaicImage(bitmap, bounds);
    }

    private Image PlaceMosaicImage(WriteableBitmap bitmap, (int X, int Y, int Width, int Height) bounds)
    {
        var origin = DipFromPixel(bounds.X, bounds.Y);
        var far = DipFromPixel(bounds.X + bounds.Width, bounds.Y + bounds.Height);
        var image = new Image
        {
            Source = bitmap,
            Width = Math.Max(1, far.X - origin.X),
            Height = Math.Max(1, far.Y - origin.Y),
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(image, origin.X);
        Canvas.SetTop(image, origin.Y);
        return image;
    }

    /// <summary>
    /// 拖拽中的马赛克图层：一块随笔迹生长的位图，只补画新增的瓦片。
    /// 拖拽时每 32ms 就要重画一次，如果每次都新建位图 + 全量重填，长笔刷会持续往 LOH 上怼 10MB 级数组。
    /// </summary>
    private sealed class MosaicLayer
    {
        public required Image Image { get; init; }
        public required WriteableBitmap Bitmap { get; init; }
        public required Stream Stream { get; init; }
        public required byte[] Pixels { get; init; }
        public required int OriginX { get; init; }
        public required int OriginY { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }

        /// <summary>已上色的瓦片（左上角像素坐标），用于增量补画。</summary>
        public HashSet<long> Drawn { get; } = [];
    }

    private MosaicLayer? _liveMosaic;

    /// <summary>本次拖拽是否已经提示过「马赛克单笔上限」，避免每帧重复弹提示。</summary>
    private bool _mosaicTruncated;

    /// <summary>丢掉拖拽图层（工具切换、落笔、重置会话时调用）。</summary>
    private void ResetLiveMosaic()
    {
        if (_liveMosaic is not { } layer)
        {
            return;
        }

        TempInk.Children.Remove(layer.Image);
        // PixelBuffer 流不会随位图一起释放，这里不 Dispose 会一直钉住非托管缓冲
        layer.Stream.Dispose();
        _liveMosaic = null;
    }

    private void DrawLiveMosaic(IReadOnlyList<(int X, int Y)> points, int radius)
    {
        var tiles = AnnotationRenderer.MosaicBrush(_source, _frameWidth, _frameHeight, points, radius, 12, out var truncated);
        if (truncated && !_mosaicTruncated)
        {
            // 上限不是静默截断：尾部瓦片已经丢掉了，必须让用户知道要松手分笔涂抹，
            // 否则预览看起来遮住了、导出的尾部其实没遮（隐私风险）。
            _mosaicTruncated = true;
            SetHint("马赛克覆盖已达单笔上限，请松开后分笔涂抹");
        }

        if (tiles.Count == 0)
        {
            return;
        }

        if (_liveMosaic is not { } layer
            || !TempInk.Children.Contains(layer.Image)
            || !Covers(layer, tiles))
        {
            var bounds = MosaicRaster.Bounds(tiles);
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }

            // 预留一圈余量，笔迹生长时不必每帧重开位图
            var pad = Math.Max(24, radius * 2);
            var left = Math.Max(0, bounds.X - pad);
            var top = Math.Max(0, bounds.Y - pad);
            var right = Math.Min(_frameWidth, bounds.X + bounds.Width + pad);
            var bottom = Math.Min(_frameHeight, bounds.Y + bounds.Height + pad);
            var next = CreateMosaicLayer(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
            MosaicRaster.Paint(next.Pixels, next.Width, next.Height, next.OriginX, next.OriginY, tiles, next.Drawn);
            FlushMosaic(next);
            ResetLiveMosaic();
            TempInk.Children.Add(next.Image);
            _liveMosaic = next;
            return;
        }

        if (MosaicRaster.Paint(layer.Pixels, layer.Width, layer.Height, layer.OriginX, layer.OriginY, tiles, layer.Drawn) > 0)
        {
            FlushMosaic(layer);
        }
    }

    private MosaicLayer CreateMosaicLayer(int x, int y, int width, int height)
    {
        var bitmap = new WriteableBitmap(width, height);
        return new MosaicLayer
        {
            Image = PlaceMosaicImage(bitmap, (x, y, width, height)),
            Bitmap = bitmap,
            Stream = bitmap.PixelBuffer.AsStream(),
            Pixels = new byte[width * height * 4],
            OriginX = x,
            OriginY = y,
            Width = width,
            Height = height,
        };
    }

    private static bool Covers(MosaicLayer layer, IReadOnlyList<AnnotationRenderer.MosaicBlock> tiles)
    {
        foreach (var tile in tiles)
        {
            if (tile.X < layer.OriginX || tile.Y < layer.OriginY
                || tile.X + tile.Width > layer.OriginX + layer.Width
                || tile.Y + tile.Height > layer.OriginY + layer.Height)
            {
                return false;
            }
        }

        return true;
    }

    private static void FlushMosaic(MosaicLayer layer)
    {
        layer.Stream.Position = 0;
        layer.Stream.Write(layer.Pixels, 0, layer.Pixels.Length);
        layer.Bitmap.Invalidate();
    }

    private void FitCanvas(Canvas canvas)
    {
        if (Root.ActualWidth > 1 && (canvas.Width != Root.ActualWidth || canvas.Height != Root.ActualHeight))
        {
            canvas.Width = Root.ActualWidth;
            canvas.Height = Root.ActualHeight;
        }
    }

    private void OpenText((int X, int Y) pixel)
    {
        CloseText(true);
        var dip = DipFromPixel(pixel.X, pixel.Y);
        _textInput = new TextBox();
        StyleTextBox(_textInput);
        Canvas.SetLeft(_textInput, dip.X);
        Canvas.SetTop(_textInput, dip.Y);
        Ui.Children.Add(_textInput);
        _textInput.Tag = pixel;
        _textInput.KeyDown += (_, args) =>
        {
            if (args.Key != VirtualKey.Enter)
            {
                return;
            }

            args.Handled = true;
            CloseText(true);
        };
        _textInput.Focus(FocusState.Programmatic);
    }

    private void CloseText(bool commit)
    {
        if (_textInput == null)
        {
            return;
        }

        var text = _textInput.Text.Trim();
        var pixel = ((int X, int Y))_textInput.Tag;
        Ui.Children.Remove(_textInput);
        _textInput = null;
        if (!commit || text.Length == 0)
        {
            return;
        }

        RememberStroke(Stroke(AnnotateKind.Text, [pixel], text));
        RebuildInk();
    }

    private void RememberStroke(AnnotateStroke stroke)
    {
        _strokes.Add(stroke);
        _redo.Clear();
    }

    private double DipWidth(AnnotateStroke stroke)
    {
        if (stroke.WidthPx <= 0 || Scale <= 0)
        {
            return _strokeDip;
        }

        return Math.Max(1, stroke.WidthPx / Scale);
    }

    private AnnotateStroke Stroke(AnnotateKind kind, List<(int X, int Y)> points, string? text)
    {
        return new AnnotateStroke
        {
            Kind = kind,
            B = _blue,
            G = _green,
            R = _red,
            Points = points,
            Text = text,
            FontPx = kind is AnnotateKind.Text or AnnotateKind.Number ? (int)Math.Round(_fontDip * Scale) : 0,
            WidthPx = kind is AnnotateKind.Text or AnnotateKind.Number ? 0 : kind == AnnotateKind.Mosaic ? BrushRadius() : StrokePixels(kind),
        };
    }

    private int StrokePixels(AnnotateKind kind)
    {
        if (kind == AnnotateKind.Text)
        {
            return 0;
        }

        return Math.Max(1, (int)Math.Round(_strokeDip * Scale, MidpointRounding.AwayFromZero));
    }

    private void StyleTextBox(TextBox box)
    {
        var color = Color.FromArgb(255, _red, _green, _blue);
        var light = _red + _green + _blue > 520;
        box.Foreground = new SolidColorBrush(color);
        box.BorderBrush = new SolidColorBrush(color);
        box.Background = new SolidColorBrush(light
            ? Color.FromArgb(230, 32, 32, 32)
            : Color.FromArgb(235, 255, 255, 255));
        box.BorderThickness = new Thickness(1.5);
        box.CornerRadius = new CornerRadius(6);
        box.FontSize = _fontDip;
        box.Padding = new Thickness(8, 4, 8, 4);
        box.MinWidth = 128;
        box.PlaceholderText = "输入文字";
    }
}
