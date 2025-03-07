using System;
using System.Collections.Generic;
using System.Linq;
using Content.Client.Alerts.UI;
using Content.Client.Chat.Managers;
using Content.Client.Resources;
using Content.Client.Stylesheets;
using Content.Shared.Chat;
using Content.Shared.Input;
using Robust.Client.AutoGenerated;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.XAML;
using Robust.Shared.Input;
using Robust.Shared.IoC;
using Robust.Shared.Localization;
using Robust.Shared.Log;
using Robust.Shared.Maths;
using Robust.Shared.Utility;
using Robust.Shared.Utility.Markup;

namespace Content.Client.Chat.UI
{
    [GenerateTypedNameReferences]
    public partial class ChatBox : Control
    {
        [Dependency] protected readonly IChatManager ChatMgr = default!;

        // order in which the available channel filters show up when available
        private static readonly ChatChannel[] ChannelFilterOrder =
        {
            ChatChannel.Local,
            ChatChannel.Emotes,
            ChatChannel.Radio,
            ChatChannel.OOC,
            ChatChannel.Dead,
            ChatChannel.Admin,
            ChatChannel.Server
        };

        // order in which the channels show up in the channel selector
        private static readonly ChatSelectChannel[] ChannelSelectorOrder =
        {
            ChatSelectChannel.Local,
            ChatSelectChannel.Emotes,
            ChatSelectChannel.Radio,
            ChatSelectChannel.OOC,
            ChatSelectChannel.Dead,
            ChatSelectChannel.Admin
            // NOTE: Console is not in there and it can never be permanently selected.
            // You can, however, still submit commands as console by prefixing with /.
        };

        public const char AliasLocal = '.';
        public const char AliasConsole = '/';
        public const char AliasDead = '\\';
        public const char AliasOOC = '[';
        public const char AliasEmotes = '@';
        public const char AliasAdmin = ']';
        public const char AliasRadio = ';';

        private static readonly Dictionary<char, ChatSelectChannel> PrefixToChannel = new()
        {
            {AliasLocal, ChatSelectChannel.Local},
            {AliasConsole, ChatSelectChannel.Console},
            {AliasOOC, ChatSelectChannel.OOC},
            {AliasEmotes, ChatSelectChannel.Emotes},
            {AliasAdmin, ChatSelectChannel.Admin},
            {AliasRadio, ChatSelectChannel.Radio},
            {AliasDead, ChatSelectChannel.Dead}
        };

        private static readonly Dictionary<ChatSelectChannel, char> ChannelPrefixes =
            PrefixToChannel.ToDictionary(kv => kv.Value, kv => kv.Key);

        private const float FilterPopupWidth = 110;

        /// <summary>
        /// The currently default channel that will be used if no prefix is specified.
        /// </summary>
        public ChatSelectChannel SelectedChannel { get; private set; } = ChatSelectChannel.OOC;

        /// <summary>
        /// The "preferred" channel. Will be switched to if permissions change and the channel becomes available,
        /// such as by re-entering body. Gets changed if the user manually selects a channel with the buttons.
        /// </summary>
        public ChatSelectChannel PreferredChannel { get; set; } = ChatSelectChannel.OOC;

        public bool ReleaseFocusOnEnter { get; set; } = true;

        private readonly Popup _channelSelectorPopup;
        private readonly BoxContainer _channelSelectorHBox;
        private readonly Popup _filterPopup;
        private readonly PanelContainer _filterPopupPanel;
        private readonly BoxContainer _filterVBox;

        /// <summary>
        /// When lobbyMode is false, will position / add to correct location in StateRoot and
        /// be resizable.
        /// wWen true, will leave layout up to parent and not be resizable.
        /// </summary>
        public ChatBox()
        {
            IoCManager.InjectDependencies(this);
            RobustXamlLoader.Load(this);

            LayoutContainer.SetMarginLeft(this, 4);
            LayoutContainer.SetMarginRight(this, 4);

            _filterPopup = new Popup
            {
                Children =
                {
                    (_filterPopupPanel = new PanelContainer
                    {
                        StyleClasses = {StyleNano.StyleClassBorderedWindowPanel},
                        Children =
                        {
                            new BoxContainer
                            {
                                Orientation = BoxContainer.LayoutOrientation.Horizontal,
                                Children =
                                {
                                    new Control {MinSize = (4, 0)},
                                    (_filterVBox = new BoxContainer
                                    {
                                        Margin = new Thickness(0, 10),
                                        Orientation = BoxContainer.LayoutOrientation.Vertical,
                                        SeparationOverride = 4
                                    })
                                }
                            }
                        }
                    })
                }
            };

            _channelSelectorPopup = new Popup
            {
                Children =
                {
                    (_channelSelectorHBox = new BoxContainer
                    {
                        Orientation = BoxContainer.LayoutOrientation.Horizontal,
                        SeparationOverride = 1
                    })
                }
            };

            ChannelSelector.OnToggled += OnChannelSelectorToggled;
            FilterButton.OnToggled += OnFilterButtonToggled;
            Input.OnKeyBindDown += InputKeyBindDown;
            Input.OnTextEntered += Input_OnTextEntered;
            Input.OnTextChanged += InputOnTextChanged;
            _channelSelectorPopup.OnPopupHide += OnChannelSelectorPopupHide;
            _filterPopup.OnPopupHide += OnFilterPopupHide;
        }

        protected override void EnteredTree()
        {
            base.EnteredTree();

            ChatMgr.MessageAdded += WriteChatMessage;
            ChatMgr.ChatPermissionsUpdated += OnChatPermissionsUpdated;
            ChatMgr.UnreadMessageCountsUpdated += UpdateUnreadMessageCounts;
            ChatMgr.FiltersUpdated += Repopulate;

            // The chat manager may have messages logged from before there was a chat box.
            // In this case, these messages will be marked as unread despite the filters allowing them through.
            // Tell chat manager to clear these.
            ChatMgr.ClearUnfilteredUnreads();

            ChatPermissionsUpdated(0);
            UpdateChannelSelectButton();
            Repopulate();
        }

        protected override void ExitedTree()
        {
            base.ExitedTree();

            ChatMgr.MessageAdded -= WriteChatMessage;
            ChatMgr.ChatPermissionsUpdated -= OnChatPermissionsUpdated;
            ChatMgr.UnreadMessageCountsUpdated -= UpdateUnreadMessageCounts;
            ChatMgr.FiltersUpdated -= Repopulate;
        }

        private void OnChatPermissionsUpdated(ChatPermissionsUpdatedEventArgs eventArgs)
        {
            ChatPermissionsUpdated(eventArgs.OldSelectableChannels);
        }

        private void ChatPermissionsUpdated(ChatSelectChannel oldSelectable)
        {
            // update the channel selector
            _channelSelectorHBox.Children.Clear();
            foreach (var selectableChannel in ChannelSelectorOrder)
            {
                if ((ChatMgr.SelectableChannels & selectableChannel) == 0)
                    continue;

                var newButton = new ChannelItemButton(selectableChannel);
                newButton.OnPressed += OnChannelSelectorItemPressed;
                _channelSelectorHBox.AddChild(newButton);
            }

            // Selected channel no longer available, switch to OOC?
            if ((ChatMgr.SelectableChannels & SelectedChannel) == 0)
            {
                // Handle local -> dead mapping when you e.g. ghost.
                // Only necessary for admins because they always have deadchat
                // so the normal preferred check won't see it as newly available and do nothing.
                var mappedSelect = MapLocalIfGhost(SelectedChannel);
                if ((ChatMgr.SelectableChannels & mappedSelect) != 0)
                    SafelySelectChannel(mappedSelect);
                else
                    SafelySelectChannel(ChatSelectChannel.OOC);
            }

            // If the preferred channel just became available, switch to it.
            var pref = MapLocalIfGhost(PreferredChannel);
            if ((oldSelectable & pref) == 0 && (ChatMgr.SelectableChannels & pref) != 0)
                SafelySelectChannel(pref);

            // update the channel filters
            _filterVBox.Children.Clear();
            foreach (var channelFilter in ChannelFilterOrder)
            {
                if ((ChatMgr.FilterableChannels & channelFilter) == 0)
                    continue;

                int? unreadCount = null;
                if (ChatMgr.UnreadMessages.TryGetValue(channelFilter, out var unread))
                    unreadCount = unread;

                var newCheckBox = new ChannelFilterCheckbox(channelFilter, unreadCount)
                {
                    Pressed = (ChatMgr.ChannelFilters & channelFilter) != 0
                };

                newCheckBox.OnToggled += OnFilterCheckboxToggled;
                _filterVBox.AddChild(newCheckBox);
            }

            UpdateChannelSelectButton();
        }

        private void UpdateUnreadMessageCounts()
        {
            foreach (var channelFilter in _filterVBox.Children)
            {
                if (channelFilter is not ChannelFilterCheckbox filterCheckbox) continue;
                if (ChatMgr.UnreadMessages.TryGetValue(filterCheckbox.Channel, out var unread))
                {
                    filterCheckbox.UpdateUnreadCount(unread);
                }
                else
                {
                    filterCheckbox.UpdateUnreadCount(null);
                }
            }
        }

        private void OnFilterCheckboxToggled(BaseButton.ButtonToggledEventArgs args)
        {
            if (args.Button is not ChannelFilterCheckbox checkbox)
                return;

            ChatMgr.OnFilterButtonToggled(checkbox.Channel, checkbox.Pressed);
        }

        private void OnFilterButtonToggled(BaseButton.ButtonToggledEventArgs args)
        {
            if (args.Pressed)
            {
                var globalPos = FilterButton.GlobalPosition;
                var (minX, minY) = _filterPopupPanel.MinSize;
                var box = UIBox2.FromDimensions(globalPos - (FilterPopupWidth, 0),
                    (Math.Max(minX, FilterPopupWidth), minY));
                UserInterfaceManager.ModalRoot.AddChild(_filterPopup);
                _filterPopup.Open(box);
            }
            else
            {
                _filterPopup.Close();
            }
        }

        private void OnChannelSelectorToggled(BaseButton.ButtonToggledEventArgs args)
        {
            if (args.Pressed)
            {
                var globalLeft = GlobalPosition.X;
                var globalBot = GlobalPosition.Y + Height;
                var box = UIBox2.FromDimensions((globalLeft, globalBot), (SizeBox.Width, AlertsUI.ChatSeparation));
                UserInterfaceManager.ModalRoot.AddChild(_channelSelectorPopup);
                _channelSelectorPopup.Open(box);
            }
            else
            {
                _channelSelectorPopup.Close();
            }
        }

        private void OnFilterPopupHide()
        {
            OnPopupHide(_filterPopup, FilterButton);
        }

        private void OnChannelSelectorPopupHide()
        {
            OnPopupHide(_channelSelectorPopup, ChannelSelector);
        }

        private void OnPopupHide(Control popup, BaseButton button)
        {
            UserInterfaceManager.ModalRoot.RemoveChild(popup);

            // this weird check here is because the hiding of the popup happens prior to the button
            // receiving the keydown, which would cause it to then become unpressed
            // and reopen immediately. To avoid this, if the popup was hidden due to clicking on the button,
            // we will not auto-unpress the button, instead leaving it up to the button toggle logic
            // (and this requires the button to be set to EnableAllKeybinds = true)
            if (UserInterfaceManager.CurrentlyHovered != button)
            {
                button.Pressed = false;
            }
        }

        private void OnChannelSelectorItemPressed(BaseButton.ButtonEventArgs obj)
        {
            if (obj.Button is not ChannelItemButton button)
                return;

            PreferredChannel = button.Channel;
            SafelySelectChannel(button.Channel);
            _channelSelectorPopup.Close();
        }

        public bool SafelySelectChannel(ChatSelectChannel toSelect)
        {
            toSelect = MapLocalIfGhost(toSelect);
            if ((ChatMgr.SelectableChannels & toSelect) == 0)
                return false;

            SelectedChannel = toSelect;
            UpdateChannelSelectButton();
            return true;
        }

        private void UpdateChannelSelectButton()
        {
            var (prefixChannel, _) = SplitInputContents();

            var channel = prefixChannel == 0 ? SelectedChannel : prefixChannel;

            ChannelSelector.Text = ChannelSelectorName(channel);
            ChannelSelector.Modulate = ChannelSelectColor(channel);
        }

        protected override void KeyBindDown(GUIBoundKeyEventArgs args)
        {
            base.KeyBindDown(args);

            if (args.CanFocus)
            {
                Input.GrabKeyboardFocus();
            }
        }

        public void CycleChatChannel(bool forward)
        {
            Input.IgnoreNext = true;

            var idx = Array.IndexOf(ChannelSelectorOrder, SelectedChannel);
            do
            {
                // go over every channel until we find one we can actually select.
                idx += forward ? 1 : -1;
                idx = MathHelper.Mod(idx, ChannelSelectorOrder.Length);
            } while ((ChatMgr.SelectableChannels & ChannelSelectorOrder[idx]) == 0);

            SafelySelectChannel(ChannelSelectorOrder[idx]);
        }

        private void Repopulate()
        {
            Contents.Clear();

            foreach (var msg in ChatMgr.History)
            {
                WriteChatMessage(msg);
            }
        }

        private void WriteChatMessage(StoredChatMessage message)
        {
            var messageText = Basic.EscapeText(message.Message);
            if (!string.IsNullOrEmpty(message.MessageWrap))
            {
                messageText = string.Format(message.MessageWrap, messageText);
            }

            Logger.DebugS("chat", $"{message.Channel}: {messageText}");

            if (IsFilteredOut(message.Channel))
                return;

            // TODO: Can make this "smarter" later by only setting it false when the message has been scrolled to
            message.Read = true;

            var color = message.MessageColorOverride != Color.Transparent
                ? message.MessageColorOverride
                : ChatHelper.ChatColor(message.Channel);

            AddLine(messageText, message.Channel, color);
        }

        private bool IsFilteredOut(ChatChannel channel)
        {
            return (ChatMgr.ChannelFilters & channel) == 0;
        }

        private void InputKeyBindDown(GUIBoundKeyEventArgs args)
        {
            if (args.Function == EngineKeyFunctions.TextReleaseFocus)
            {
                Input.ReleaseKeyboardFocus();
                args.Handle();
                return;
            }

            if (args.Function == ContentKeyFunctions.CycleChatChannelForward)
            {
                CycleChatChannel(true);
                args.Handle();
                return;
            }

            if (args.Function == ContentKeyFunctions.CycleChatChannelBackward)
            {
                CycleChatChannel(false);
                args.Handle();
            }
        }

        private (ChatSelectChannel selChannel, ReadOnlyMemory<char> text) SplitInputContents()
        {
            var text = Input.Text.AsMemory().Trim();
            if (text.Length == 0)
                return default;

            var prefixChar = text.Span[0];
            var channel = GetChannelFromPrefix(prefixChar);

            if ((ChatMgr.SelectableChannels & channel) != 0)
                // Cut off prefix if it's valid and we can use the channel in question.
                text = text[1..];
            else
                channel = 0;

            channel = MapLocalIfGhost(channel);

            // Trim from start again to cut out any whitespace between the prefix and message, if any.
            return (channel, text.TrimStart());
        }

        private void InputOnTextChanged(LineEdit.LineEditEventArgs obj)
        {
            // Update channel select button to correct channel if we have a prefix.
            UpdateChannelSelectButton();
        }

        private static ChatSelectChannel GetChannelFromPrefix(char prefix)
        {
            return PrefixToChannel.GetValueOrDefault(prefix);
        }

        public static char GetPrefixFromChannel(ChatSelectChannel channel)
        {
            return ChannelPrefixes.GetValueOrDefault(channel);
        }

        public static string ChannelSelectorName(ChatSelectChannel channel)
        {
            return Loc.GetString($"hud-chatbox-select-channel-{channel}");
        }

        public static Color ChannelSelectColor(ChatSelectChannel channel)
        {
            return channel switch
            {
                ChatSelectChannel.Radio => Color.LimeGreen,
                ChatSelectChannel.OOC => Color.LightSkyBlue,
                ChatSelectChannel.Dead => Color.MediumPurple,
                ChatSelectChannel.Admin => Color.Red,
                _ => Color.DarkGray
            };
        }

        public void AddLine(string message, ChatChannel channel, Color color)
        {
            DebugTools.Assert(!Disposed);

            Contents.AddMessage(Basic.RenderMarkup(message, new Section{Color=color.ToArgb()}));
        }

        private void Input_OnTextEntered(LineEdit.LineEditEventArgs args)
        {
            if (!string.IsNullOrWhiteSpace(args.Text))
            {
                var (prefixChannel, text) = SplitInputContents();

                // Check if message is longer than the character limit
                if (text.Length > ChatMgr.MaxMessageLength)
                {
                    string locWarning = Loc.GetString(
                        "chat-manager-max-message-length",
                        ("maxMessageLength", ChatMgr.MaxMessageLength));

                    AddLine(locWarning, ChatChannel.Server, Color.Orange);
                    return;
                }

                ChatMgr.OnChatBoxTextSubmitted(this, text, prefixChannel == 0 ? SelectedChannel : prefixChannel);
            }

            Input.Clear();
            UpdateChannelSelectButton();

            if (ReleaseFocusOnEnter)
                Input.ReleaseKeyboardFocus();
        }

        public void Focus(ChatSelectChannel? channel = null)
        {
            var selectStart = Index.End;
            if (channel != null)
            {
                channel = MapLocalIfGhost(channel.Value);

                // Channel not selectable, just do NOTHING (not even focus).
                if (!((ChatMgr.SelectableChannels & channel.Value) != 0))
                    return;

                var (_, text) = SplitInputContents();

                var newPrefix = GetPrefixFromChannel(channel.Value);
                DebugTools.Assert(newPrefix != default, "Focus channel must have prefix!");

                if (channel == SelectedChannel)
                {
                    // New selected channel is just the selected channel,
                    // just remove prefix (if any) and leave text unchanged.

                    Input.Text = text.ToString();
                    selectStart = Index.Start;
                }
                else
                {
                    // Change prefix to new focused channel prefix and leave text unchanged.
                    Input.Text = string.Concat(newPrefix.ToString(), " ", text.Span);
                    selectStart = Index.FromStart(2);
                }

                UpdateChannelSelectButton();
            }

            Input.IgnoreNext = true;
            Input.GrabKeyboardFocus();

            Input.CursorPosition = Input.Text.Length;
            Input.SelectionStart = selectStart.GetOffset(Input.Text.Length);
        }

        private ChatSelectChannel MapLocalIfGhost(ChatSelectChannel channel)
        {
            if (channel == ChatSelectChannel.Local && ChatMgr.IsGhost)
                return ChatSelectChannel.Dead;

            return channel;
        }
    }

    /// <summary>
    /// Only needed to avoid the issue where right click on the button closes the popup
    /// but leaves the button highlighted.
    /// </summary>
    public sealed class ChannelSelectorButton : Button
    {
        public ChannelSelectorButton()
        {
            // needed so the popup is untoggled regardless of which key is pressed when hovering this button.
            // If we don't have this, then right clicking the button while it's toggled on will hide
            // the popup but keep the button toggled on
            Mode = ActionMode.Press;
            EnableAllKeybinds = true;
        }

        protected override void KeyBindDown(GUIBoundKeyEventArgs args)
        {
            // needed since we need EnableAllKeybinds - don't double-send both UI click and Use
            if (args.Function == EngineKeyFunctions.Use)
                return;

            base.KeyBindDown(args);
        }
    }

    public sealed class FilterButton : ContainerButton
    {
        private static readonly Color ColorNormal = Color.FromHex("#7b7e9e");
        private static readonly Color ColorHovered = Color.FromHex("#9699bb");
        private static readonly Color ColorPressed = Color.FromHex("#789B8C");

        private readonly TextureRect _textureRect;

        public FilterButton()
        {
            var filterTexture = IoCManager.Resolve<IResourceCache>()
                .GetTexture("/Textures/Interface/Nano/filter.svg.96dpi.png");

            // needed for same reason as ChannelSelectorButton
            Mode = ActionMode.Press;
            EnableAllKeybinds = true;

            AddChild(
                (_textureRect = new TextureRect
                {
                    Texture = filterTexture,
                    HorizontalAlignment = HAlignment.Center,
                    VerticalAlignment = VAlignment.Center
                })
            );

            ToggleMode = true;
        }

        protected override void KeyBindDown(GUIBoundKeyEventArgs args)
        {
            // needed since we need EnableAllKeybinds - don't double-send both UI click and Use
            if (args.Function == EngineKeyFunctions.Use) return;
            base.KeyBindDown(args);
        }

        private void UpdateChildColors()
        {
            if (_textureRect == null) return;
            switch (DrawMode)
            {
                case DrawModeEnum.Normal:
                    _textureRect.ModulateSelfOverride = ColorNormal;
                    break;

                case DrawModeEnum.Pressed:
                    _textureRect.ModulateSelfOverride = ColorPressed;
                    break;

                case DrawModeEnum.Hover:
                    _textureRect.ModulateSelfOverride = ColorHovered;
                    break;

                case DrawModeEnum.Disabled:
                    break;
            }
        }

        protected override void DrawModeChanged()
        {
            base.DrawModeChanged();
            UpdateChildColors();
        }

        protected override void StylePropertiesChanged()
        {
            base.StylePropertiesChanged();
            UpdateChildColors();
        }
    }

    public sealed class ChannelItemButton : Button
    {
        public readonly ChatSelectChannel Channel;

        public ChannelItemButton(ChatSelectChannel channel)
        {
            Channel = channel;
            AddStyleClass(StyleNano.StyleClassChatChannelSelectorButton);
            Text = ChatBox.ChannelSelectorName(channel);

            var prefix = ChatBox.GetPrefixFromChannel(channel);
            if (prefix != default)
                Text = Loc.GetString("hud-chatbox-select-name-prefixed", ("name", Text), ("prefix", prefix));
        }
    }

    public sealed class ChannelFilterCheckbox : CheckBox
    {
        public readonly ChatChannel Channel;

        public ChannelFilterCheckbox(ChatChannel channel, int? unreadCount)
        {
            Channel = channel;

            UpdateText(unreadCount);
        }

        private void UpdateText(int? unread)
        {
            var name = Loc.GetString($"hud-chatbox-channel-{Channel}");

            if (unread > 0)
                // todo: proper fluent stuff here.
                name += " (" + (unread > 9 ? "9+" : unread) + ")";

            Text = name;
        }

        public void UpdateUnreadCount(int? unread)
        {
            UpdateText(unread);
        }
    }

    public readonly struct ChatResizedEventArgs
    {
        /// new bottom that the chat rect is going to have in virtual pixels
        /// after the imminent relayout
        public readonly float NewBottom;

        public ChatResizedEventArgs(float newBottom)
        {
            NewBottom = newBottom;
        }
    }
}
