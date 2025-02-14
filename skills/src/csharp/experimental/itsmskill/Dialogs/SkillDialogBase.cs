﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ITSMSkill.Models;
using ITSMSkill.Prompts;
using ITSMSkill.Responses.Shared;
using ITSMSkill.Responses.Ticket;
using ITSMSkill.Services;
using ITSMSkill.Utilities;
using Luis;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Builder.Dialogs.Choices;
using Microsoft.Bot.Builder.Skills;
using Microsoft.Bot.Builder.Solutions.Authentication;
using Microsoft.Bot.Builder.Solutions.Responses;
using Microsoft.Bot.Builder.Solutions.Util;
using Microsoft.Bot.Connector;
using Microsoft.Bot.Schema;
using Microsoft.Graph;

namespace ITSMSkill.Dialogs
{
    public class SkillDialogBase : ComponentDialog
    {
        public SkillDialogBase(
             string dialogId,
             BotSettings settings,
             BotServices services,
             ResponseManager responseManager,
             ConversationState conversationState,
             IServiceManager serviceManager,
             IBotTelemetryClient telemetryClient)
             : base(dialogId)
        {
            Settings = settings;
            Services = services;
            ResponseManager = responseManager;
            StateAccessor = conversationState.CreateProperty<SkillState>(nameof(SkillState));
            ServiceManager = serviceManager;
            TelemetryClient = telemetryClient;

            // NOTE: Uncomment the following if your skill requires authentication
            if (!settings.OAuthConnections.Any())
            {
                throw new Exception("You must configure an authentication connection before using this component.");
            }

            AddDialog(new MultiProviderAuthDialog(settings.OAuthConnections));

            var setDescription = new WaterfallStep[]
            {
                CheckDescription,
                InputDescription,
                SetDescription
            };

            var setUrgency = new WaterfallStep[]
            {
                CheckUrgency,
                InputUrgency,
                SetUrgency
            };

            var setId = new WaterfallStep[]
            {
                CheckId,
                InputId,
                SetId
            };

            var setState = new WaterfallStep[]
            {
                CheckState,
                InputState,
                SetState
            };

            // TODO since number is ServiceNow specific regex, no need to check
            var setNumber = new WaterfallStep[]
            {
                InputTicketNumber,
                SetTicketNumber,
            };

            var setNumberThenId = new WaterfallStep[]
            {
                InputTicketNumber,
                SetTicketNumber,
                GetAuthToken,
                AfterGetAuthToken,
                SetIdFromNumber,
            };

            var baseAuth = new WaterfallStep[]
            {
                GetAuthToken,
                AfterGetAuthToken,
                BeginInitialDialog
            };

            var navigateYesNo = new HashSet<GeneralLuis.Intent>()
            {
                GeneralLuis.Intent.ShowNext,
                GeneralLuis.Intent.ShowPrevious,
                GeneralLuis.Intent.Confirm,
                GeneralLuis.Intent.Reject
            };

            var navigateNo = new HashSet<GeneralLuis.Intent>()
            {
                GeneralLuis.Intent.ShowNext,
                GeneralLuis.Intent.ShowPrevious,
                GeneralLuis.Intent.Reject
            };

            AddDialog(new TextPrompt(nameof(TextPrompt)));
            AddDialog(new ConfirmPrompt(nameof(ConfirmPrompt)));
            AddDialog(new ChoicePrompt(nameof(ChoicePrompt)));
            AddDialog(new TicketNumberPrompt(nameof(TicketNumberPrompt)));
            AddDialog(new WaterfallDialog(Actions.SetDescription, setDescription));
            AddDialog(new WaterfallDialog(Actions.SetUrgency, setUrgency));
            AddDialog(new WaterfallDialog(Actions.SetId, setId));
            AddDialog(new WaterfallDialog(Actions.SetState, setState));
            AddDialog(new WaterfallDialog(Actions.SetNumber, setNumber));
            AddDialog(new WaterfallDialog(Actions.SetNumberThenId, setNumberThenId));
            AddDialog(new WaterfallDialog(Actions.BaseAuth, baseAuth));
            AddDialog(new GeneralPrompt(Actions.NavigateYesNoPrompt, navigateYesNo, StateAccessor));
            AddDialog(new GeneralPrompt(Actions.NavigateNoPrompt, navigateNo, StateAccessor));

            base.InitialDialogId = Actions.BaseAuth;
        }

        protected BotSettings Settings { get; set; }

        protected BotServices Services { get; set; }

        protected IStatePropertyAccessor<SkillState> StateAccessor { get; set; }

        protected ResponseManager ResponseManager { get; set; }

        protected IServiceManager ServiceManager { get; set; }

        protected new string InitialDialogId { get; set; }

        protected string ConfirmAttributeResponse { get; set; }

        protected string InputAttributeResponse { get; set; }

        protected string InputAttributePrompt { get; set; }

        protected string ShowKnowledgeNoResponse { get; set; }

        protected string ShowKnowledgeEndResponse { get; set; }

        protected string ShowKnowledgeResponse { get; set; }

        protected string ShowKnowledgePrompt { get; set; }

        protected string KnowledgeHelpLoop { get; set; }

        protected override async Task<DialogTurnResult> OnBeginDialogAsync(DialogContext dc, object options, CancellationToken cancellationToken = default(CancellationToken))
        {
            return await base.OnBeginDialogAsync(dc, options, cancellationToken);
        }

        protected override async Task<DialogTurnResult> OnContinueDialogAsync(DialogContext dc, CancellationToken cancellationToken = default(CancellationToken))
        {
            return await base.OnContinueDialogAsync(dc, cancellationToken);
        }

        protected async Task<DialogTurnResult> GetAuthToken(WaterfallStepContext sc, CancellationToken cancellationToken)
        {
            try
            {
                return await sc.PromptAsync(nameof(MultiProviderAuthDialog), new PromptOptions());
            }
            catch (SkillException ex)
            {
                await HandleDialogExceptions(sc, ex);
                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);
                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        protected async Task<DialogTurnResult> AfterGetAuthToken(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                var state = await StateAccessor.GetAsync(sc.Context);

                // When the user authenticates interactively we pass on the tokens/Response event which surfaces as a JObject
                // When the token is cached we get a TokenResponse object.
                if (sc.Result is ProviderTokenResponse providerTokenResponse)
                {
                    state.Token = providerTokenResponse.TokenResponse;
                }
                else
                {
                    state.Token = null;
                }

                if (state.Token == null)
                {
                    await sc.Context.SendActivityAsync(ResponseManager.GetResponse(SharedResponses.AuthFailed));
                    return await sc.CancelAllDialogsAsync();
                }

                return await sc.NextAsync();
            }
            catch (SkillException ex)
            {
                await HandleDialogExceptions(sc, ex);
                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);
                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        protected async Task<DialogTurnResult> BeginInitialDialog(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            return await sc.BeginDialogAsync(InitialDialogId);
        }

        protected async Task<DialogTurnResult> CheckId(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            var state = await StateAccessor.GetAsync(sc.Context, () => new SkillState());
            if (string.IsNullOrEmpty(state.Id))
            {
                return await sc.NextAsync(false);
            }
            else
            {
                var replacements = new StringDictionary
                {
                    { "Id", state.Id }
                };

                var options = new PromptOptions()
                {
                    Prompt = ResponseManager.GetResponse(SharedResponses.ConfirmId, replacements)
                };

                return await sc.PromptAsync(nameof(ConfirmPrompt), options);
            }
        }

        protected async Task<DialogTurnResult> InputId(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            var state = await StateAccessor.GetAsync(sc.Context, () => new SkillState());
            if (!(bool)sc.Result || string.IsNullOrEmpty(state.Id))
            {
                var options = new PromptOptions()
                {
                    Prompt = ResponseManager.GetResponse(SharedResponses.InputId)
                };

                return await sc.PromptAsync(nameof(TextPrompt), options);
            }
            else
            {
                return await sc.NextAsync(state.Id);
            }
        }

        protected async Task<DialogTurnResult> SetId(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            var state = await StateAccessor.GetAsync(sc.Context, () => new SkillState());
            state.Id = (string)sc.Result;
            return await sc.NextAsync();
        }

        protected async Task<DialogTurnResult> CheckAttribute(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            var state = await StateAccessor.GetAsync(sc.Context, () => new SkillState());
            if (state.AttributeType == AttributeType.None)
            {
                return await sc.NextAsync(false);
            }
            else
            {
                var replacements = new StringDictionary
                {
                    { "Attribute", state.AttributeType.ToLocalizedString() }
                };

                var options = new PromptOptions()
                {
                    Prompt = ResponseManager.GetResponse(ConfirmAttributeResponse, replacements)
                };

                return await sc.PromptAsync(nameof(ConfirmPrompt), options);
            }
        }

        protected async Task<DialogTurnResult> InputAttribute(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            var state = await StateAccessor.GetAsync(sc.Context, () => new SkillState());
            if (!(bool)sc.Result || state.AttributeType == AttributeType.None)
            {
                var options = new PromptOptions()
                {
                    Prompt = ResponseManager.GetResponse(InputAttributeResponse)
                };

                return await sc.PromptAsync(InputAttributePrompt, options);
            }
            else
            {
                return await sc.NextAsync(state.AttributeType);
            }
        }

        protected async Task<DialogTurnResult> SetAttribute(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (sc.Result == null)
            {
                return await sc.EndDialogAsync();
            }

            var state = await StateAccessor.GetAsync(sc.Context, () => new SkillState());
            state.AttributeType = (AttributeType)sc.Result;
            return await sc.NextAsync();
        }

        protected async Task<DialogTurnResult> UpdateSelectedAttribute(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            var state = await StateAccessor.GetAsync(sc.Context, () => new SkillState());
            var attribute = state.AttributeType;
            state.AttributeType = AttributeType.None;
            if (attribute == AttributeType.Description)
            {
                state.TicketDescription = null;
                return await sc.BeginDialogAsync(Actions.SetDescription);
            }
            else if (attribute == AttributeType.Urgency)
            {
                state.UrgencyLevel = UrgencyLevel.None;
                return await sc.BeginDialogAsync(Actions.SetUrgency);
            }
            else if (attribute == AttributeType.Id)
            {
                state.Id = null;
                return await sc.BeginDialogAsync(Actions.SetId);
            }
            else if (attribute == AttributeType.State)
            {
                state.TicketState = TicketState.None;
                return await sc.BeginDialogAsync(Actions.SetState);
            }
            else if (attribute == AttributeType.Number)
            {
                state.TicketNumber = null;
                return await sc.BeginDialogAsync(Actions.SetNumber);
            }
            else
            {
                throw new Exception($"Invalid AttributeType: {attribute}");
            }
        }

        protected async Task<DialogTurnResult> CheckDescription(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            var state = await StateAccessor.GetAsync(sc.Context, () => new SkillState());
            if (string.IsNullOrEmpty(state.TicketDescription))
            {
                return await sc.NextAsync(false);
            }
            else
            {
                var replacements = new StringDictionary
                {
                    { "Description", state.TicketDescription }
                };

                var options = new PromptOptions()
                {
                    Prompt = ResponseManager.GetResponse(SharedResponses.ConfirmDescription, replacements)
                };

                return await sc.PromptAsync(nameof(ConfirmPrompt), options);
            }
        }

        protected async Task<DialogTurnResult> InputDescription(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            var state = await StateAccessor.GetAsync(sc.Context, () => new SkillState());
            if (!(bool)sc.Result || string.IsNullOrEmpty(state.TicketDescription))
            {
                var options = new PromptOptions()
                {
                    Prompt = ResponseManager.GetResponse(SharedResponses.InputDescription)
                };

                return await sc.PromptAsync(nameof(TextPrompt), options);
            }
            else
            {
                return await sc.NextAsync(state.TicketDescription);
            }
        }

        protected async Task<DialogTurnResult> SetDescription(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            var state = await StateAccessor.GetAsync(sc.Context, () => new SkillState());
            state.TicketDescription = (string)sc.Result;
            return await sc.NextAsync();
        }

        protected async Task<DialogTurnResult> CheckReason(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            var state = await StateAccessor.GetAsync(sc.Context, () => new SkillState());
            if (string.IsNullOrEmpty(state.CloseReason))
            {
                return await sc.NextAsync(false);
            }
            else
            {
                var replacements = new StringDictionary
                {
                    { "Reason", state.CloseReason }
                };

                var options = new PromptOptions()
                {
                    Prompt = ResponseManager.GetResponse(SharedResponses.ConfirmReason, replacements)
                };

                return await sc.PromptAsync(nameof(ConfirmPrompt), options);
            }
        }

        protected async Task<DialogTurnResult> InputReason(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            var state = await StateAccessor.GetAsync(sc.Context, () => new SkillState());
            if (!(bool)sc.Result || string.IsNullOrEmpty(state.CloseReason))
            {
                var options = new PromptOptions()
                {
                    Prompt = ResponseManager.GetResponse(SharedResponses.InputReason)
                };

                return await sc.PromptAsync(nameof(TextPrompt), options);
            }
            else
            {
                return await sc.NextAsync(state.CloseReason);
            }
        }

        protected async Task<DialogTurnResult> SetReason(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            var state = await StateAccessor.GetAsync(sc.Context, () => new SkillState());
            state.CloseReason = (string)sc.Result;
            return await sc.NextAsync();
        }

        protected async Task<DialogTurnResult> InputTicketNumber(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            var state = await StateAccessor.GetAsync(sc.Context, () => new SkillState());
            if (string.IsNullOrEmpty(state.TicketNumber))
            {
                var options = new PromptOptions()
                {
                    Prompt = ResponseManager.GetResponse(SharedResponses.InputTicketNumber)
                };

                return await sc.PromptAsync(nameof(TicketNumberPrompt), options);
            }
            else
            {
                return await sc.NextAsync(state.TicketNumber);
            }
        }

        protected async Task<DialogTurnResult> SetTicketNumber(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            var state = await StateAccessor.GetAsync(sc.Context, () => new SkillState());
            state.TicketNumber = (string)sc.Result;
            return await sc.NextAsync();
        }

        protected async Task<DialogTurnResult> SetIdFromNumber(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            var state = await StateAccessor.GetAsync(sc.Context, () => new SkillState());
            var management = ServiceManager.CreateManagement(Settings, state.Token);
            var result = await management.SearchTicket(0, number: state.TicketNumber);

            if (!result.Success)
            {
                return await SendServiceErrorAndCancel(sc, result);
            }

            if (result.Tickets == null || result.Tickets.Length == 0)
            {
                await sc.Context.SendActivityAsync(ResponseManager.GetResponse(TicketResponses.TicketFindNone));
                return await sc.CancelAllDialogsAsync();
            }

            if (result.Tickets.Length >= 2)
            {
                await sc.Context.SendActivityAsync(ResponseManager.GetResponse(TicketResponses.TicketDuplicateNumber));
                return await sc.CancelAllDialogsAsync();
            }

            state.TicketTarget = result.Tickets[0];
            state.Id = state.TicketTarget.Id;

            var card = new Card()
            {
                Name = GetDivergedCardName(sc.Context, "Ticket"),
                Data = ConvertTicket(state.TicketTarget)
            };

            await sc.Context.SendActivityAsync(ResponseManager.GetCardResponse(TicketResponses.TicketTarget, card, null));
            return await sc.NextAsync();
        }

        protected async Task<DialogTurnResult> BeginSetNumberThenId(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            return await sc.BeginDialogAsync(Actions.SetNumberThenId);
        }

        protected async Task<DialogTurnResult> CheckUrgency(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            var state = await StateAccessor.GetAsync(sc.Context, () => new SkillState());
            if (state.UrgencyLevel == UrgencyLevel.None)
            {
                return await sc.NextAsync(false);
            }
            else
            {
                var replacements = new StringDictionary
                {
                    { "Urgency", state.UrgencyLevel.ToString() }
                };

                var options = new PromptOptions()
                {
                    Prompt = ResponseManager.GetResponse(SharedResponses.ConfirmUrgency, replacements)
                };

                return await sc.PromptAsync(nameof(ConfirmPrompt), options);
            }
        }

        protected async Task<DialogTurnResult> InputUrgency(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            var state = await StateAccessor.GetAsync(sc.Context, () => new SkillState());
            if (!(bool)sc.Result || state.UrgencyLevel == UrgencyLevel.None)
            {
                var options = new PromptOptions()
                {
                    Prompt = ResponseManager.GetResponse(SharedResponses.InputUrgency),
                    Choices = new List<Choice>()
                    {
                        new Choice()
                        {
                            Value = UrgencyLevel.Low.ToLocalizedString()
                        },
                        new Choice()
                        {
                            Value = UrgencyLevel.Medium.ToLocalizedString()
                        },
                        new Choice()
                        {
                            Value = UrgencyLevel.High.ToLocalizedString()
                        }
                    }
                };

                return await sc.PromptAsync(nameof(ChoicePrompt), options);
            }
            else
            {
                // use Index to skip localization
                return await sc.NextAsync(new FoundChoice()
                {
                    Index = (int)state.UrgencyLevel - 1
                });
            }
        }

        protected async Task<DialogTurnResult> SetUrgency(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            var state = await StateAccessor.GetAsync(sc.Context, () => new SkillState());
            state.UrgencyLevel = (UrgencyLevel)(((FoundChoice)sc.Result).Index + 1);
            return await sc.NextAsync();
        }

        protected async Task<DialogTurnResult> CheckState(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            var state = await StateAccessor.GetAsync(sc.Context, () => new SkillState());
            if (state.TicketState == TicketState.None)
            {
                return await sc.NextAsync(false);
            }
            else
            {
                var replacements = new StringDictionary
                {
                    { "State", state.TicketState.ToString() }
                };

                var options = new PromptOptions()
                {
                    Prompt = ResponseManager.GetResponse(SharedResponses.ConfirmState, replacements)
                };

                return await sc.PromptAsync(nameof(ConfirmPrompt), options);
            }
        }

        protected async Task<DialogTurnResult> InputState(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            var state = await StateAccessor.GetAsync(sc.Context, () => new SkillState());
            if (!(bool)sc.Result || state.TicketState == TicketState.None)
            {
                var options = new PromptOptions()
                {
                    Prompt = ResponseManager.GetResponse(SharedResponses.InputState),
                    Choices = new List<Choice>()
                    {
                        new Choice()
                        {
                            Value = TicketState.New.ToLocalizedString()
                        },
                        new Choice()
                        {
                            Value = TicketState.InProgress.ToLocalizedString()
                        },
                        new Choice()
                        {
                            Value = TicketState.OnHold.ToLocalizedString()
                        },
                        new Choice()
                        {
                            Value = TicketState.Resolved.ToLocalizedString()
                        },
                        new Choice()
                        {
                            Value = TicketState.Closed.ToLocalizedString()
                        },
                        new Choice()
                        {
                            Value = TicketState.Canceled.ToLocalizedString()
                        }
                    }
                };

                return await sc.PromptAsync(nameof(ChoicePrompt), options);
            }
            else
            {
                // use Index to skip localization
                return await sc.NextAsync(new FoundChoice()
                {
                    Index = (int)state.TicketState - 1
                });
            }
        }

        protected async Task<DialogTurnResult> SetState(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            var state = await StateAccessor.GetAsync(sc.Context, () => new SkillState());
            state.TicketState = (TicketState)(((FoundChoice)sc.Result).Index + 1);
            return await sc.NextAsync();
        }

        protected async Task<DialogTurnResult> ShowKnowledge(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            var state = await StateAccessor.GetAsync(sc.Context, () => new SkillState());

            bool firstDisplay = false;
            if (state.PageIndex == -1)
            {
                firstDisplay = true;
                state.PageIndex = 0;
            }

            var management = ServiceManager.CreateManagement(Settings, state.Token);
            var result = await management.SearchKnowledge(state.TicketDescription, state.PageIndex);

            if (!result.Success)
            {
                return await SendServiceErrorAndCancel(sc, result);
            }

            if (result.Knowledges == null || result.Knowledges.Length == 0)
            {
                if (firstDisplay)
                {
                    if (!string.IsNullOrEmpty(ShowKnowledgeNoResponse))
                    {
                        await sc.Context.SendActivityAsync(ResponseManager.GetResponse(ShowKnowledgeNoResponse));
                    }

                    return await sc.EndDialogAsync();
                }
                else
                {
                    var token = new StringDictionary()
                    {
                        { "Page", (state.PageIndex + 1).ToString() }
                    };

                    var options = new PromptOptions()
                    {
                        Prompt = ResponseManager.GetResponse(ShowKnowledgeEndResponse, token)
                    };

                    return await sc.PromptAsync(ShowKnowledgePrompt, options);
                }
            }
            else
            {
                var cards = new List<Card>();
                foreach (var knowledge in result.Knowledges)
                {
                    cards.Add(new Card()
                    {
                        Name = GetDivergedCardName(sc.Context, "Knowledge"),
                        Data = ConvertKnowledge(knowledge)
                    });
                }

                var token = new StringDictionary()
                {
                    { "Page", (state.PageIndex + 1).ToString() }
                };

                var options = new PromptOptions()
                {
                    Prompt = ResponseManager.GetCardResponse(ShowKnowledgeResponse, cards, token)
                };

                // Workaround. In teams, HeroCard will be used for prompt and adaptive card could not be shown. So send them separatly
                if (Channel.GetChannelId(sc.Context) == Channels.Msteams)
                {
                    await sc.Context.SendActivityAsync(options.Prompt);
                    options.Prompt = null;
                }

                return await sc.PromptAsync(ShowKnowledgePrompt, options);
            }
        }

        protected async Task<DialogTurnResult> IfKnowledgeHelp(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            var intent = (GeneralLuis.Intent)sc.Result;
            if (intent == GeneralLuis.Intent.Confirm)
            {
                await sc.Context.SendActivityAsync(ResponseManager.GetResponse(SharedResponses.ActionEnded));
                return await sc.CancelAllDialogsAsync();
            }
            else if (intent == GeneralLuis.Intent.Reject)
            {
                return await sc.EndDialogAsync();
            }
            else if (intent == GeneralLuis.Intent.ShowNext)
            {
                var state = await StateAccessor.GetAsync(sc.Context, () => new SkillState());
                state.PageIndex += 1;
                return await sc.ReplaceDialogAsync(KnowledgeHelpLoop);
            }
            else if (intent == GeneralLuis.Intent.ShowPrevious)
            {
                var state = await StateAccessor.GetAsync(sc.Context, () => new SkillState());
                state.PageIndex = Math.Max(0, state.PageIndex - 1);
                return await sc.ReplaceDialogAsync(KnowledgeHelpLoop);
            }
            else
            {
                throw new Exception($"Invalid GeneralLuis.Intent ${intent}");
            }
        }

        // Validators
        protected Task<bool> TokenResponseValidator(PromptValidatorContext<Activity> pc, CancellationToken cancellationToken)
        {
            var activity = pc.Recognized.Value;
            if (activity != null && activity.Type == ActivityTypes.Event)
            {
                return Task.FromResult(true);
            }
            else
            {
                return Task.FromResult(false);
            }
        }

        protected Task<bool> AuthPromptValidator(PromptValidatorContext<TokenResponse> promptContext, CancellationToken cancellationToken)
        {
            var token = promptContext.Recognized.Value;
            if (token != null)
            {
                return Task.FromResult(true);
            }
            else
            {
                return Task.FromResult(false);
            }
        }

        // Helpers
        // This method is called by any waterfall step that throws an exception to ensure consistency
        protected async Task HandleDialogExceptions(WaterfallStepContext sc, Exception ex)
        {
            // send trace back to emulator
            var trace = new Activity(type: ActivityTypes.Trace, text: $"DialogException: {ex.Message}, StackTrace: {ex.StackTrace}");
            await sc.Context.SendActivityAsync(trace);

            // log exception
            TelemetryClient.TrackException(ex, new Dictionary<string, string> { { nameof(sc.ActiveDialog), sc.ActiveDialog?.Id } });

            // send error message to bot user
            await sc.Context.SendActivityAsync(ResponseManager.GetResponse(SharedResponses.ErrorMessage));

            // clear state
            var state = await StateAccessor.GetAsync(sc.Context);
            state.ClearLuisResult();
        }

        protected async Task<DialogTurnResult> SendServiceErrorAndCancel(WaterfallStepContext sc, ResultBase result)
        {
            var errorReplacements = new StringDictionary
            {
                { "Error", result.ErrorMessage }
            };
            await sc.Context.SendActivityAsync(ResponseManager.GetResponse(SharedResponses.ServiceFailed, errorReplacements));
            return await sc.CancelAllDialogsAsync();
        }

        protected TicketCard ConvertTicket(Ticket ticket)
        {
            var card = new TicketCard()
            {
                Description = ticket.Description,
                UrgencyLevel = $"{SharedStrings.Urgency}{ticket.Urgency.ToLocalizedString()}",
                State = $"{SharedStrings.TicketState}{ticket.State.ToLocalizedString()}",
                OpenedTime = $"{SharedStrings.OpenedAt}{ticket.OpenedTime.ToString()}",
                Id = $"{SharedStrings.ID}{ticket.Id}",
                ResolvedReason = ticket.ResolvedReason,
                Speak = ticket.Description,
                Number = $"{SharedStrings.TicketNumber}{ticket.Number}",
            };
            return card;
        }

        protected KnowledgeCard ConvertKnowledge(Knowledge knowledge)
        {
            var card = new KnowledgeCard()
            {
                Id = $"{SharedStrings.ID}{knowledge.Id}",
                Title = knowledge.Title,
                UpdatedTime = $"{SharedStrings.UpdatedAt}{knowledge.UpdatedTime.ToString()}",
                Content = knowledge.Content,
                Speak = knowledge.Title,
                Number = $"{SharedStrings.TicketNumber}{knowledge.Number}",
                UrlTitle = SharedStrings.OpenKnowledge,
                UrlLink = knowledge.Url,
            };
            return card;
        }

        protected string GetDivergedCardName(ITurnContext turnContext, string card)
        {
            if (Channel.GetChannelId(turnContext) == Channels.Msteams)
            {
                return card + ".1.0";
            }
            else
            {
                return card;
            }
        }

        protected class Actions
        {
            public const string SetDescription = "SetDescription";
            public const string SetUrgency = "SetUrgency";
            public const string SetId = "SetId";
            public const string SetState = "SetState";
            public const string SetNumber = "SetNumber";
            public const string SetNumberThenId = "SetNumberThenId";

            public const string BaseAuth = "BaseAuth";

            public const string NavigateYesNoPrompt = "NavigateYesNoPrompt";
            public const string NavigateNoPrompt = "NavigateNoPrompt";

            public const string CreateTicket = "CreateTicket";
            public const string DisplayExisting = "DisplayExisting";

            public const string UpdateTicket = "UpdateTicket";
            public const string UpdateAttribute = "UpdateAttribute";
            public const string UpdateAttributePrompt = "UpdateAttributePrompt";

            public const string ShowTicket = "ShowTicket";
            public const string ShowAttribute = "ShowAttribute";
            public const string ShowAttributePrompt = "ShowAttributePrompt";
            public const string ShowTicketLoop = "ShowTicketLoop";

            public const string CloseTicket = "CloseTicket";

            public const string ShowKnowledge = "ShowKnowledge";
            public const string ShowKnowledgeLoop = "ShowKnowledgeLoop";
        }
    }
}
