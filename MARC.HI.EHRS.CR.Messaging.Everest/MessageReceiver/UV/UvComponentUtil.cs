﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MARC.Everest.Connectors;
using MARC.HI.EHRS.CR.Core.ComponentModel;
using MARC.HI.EHRS.SVC.Core.Services;
using MARC.HI.EHRS.SVC.Core.ComponentModel.Components;
using MARC.HI.EHRS.SVC.Core.ComponentModel;
using MARC.Everest.DataTypes;
using MARC.Everest.RMIM.UV.NE2008.Vocabulary;
using System.ComponentModel;
using MARC.HI.EHRS.SVC.Core.DataTypes;
using MARC.Everest.RMIM.UV.NE2008.Interactions;

namespace MARC.HI.EHRS.CR.Messaging.Everest.MessageReceiver.UV
{
    /// <summary>
    /// Universal component utility
    /// </summary>
    public class UvComponentUtil : ComponentUtil
    {

        /// <summary>
        /// Create components for the registration event based on the control act process
        /// </summary>
        private RegistrationEvent CreateComponents<T, U>(MARC.Everest.RMIM.UV.NE2008.MFMI_MT700701UV01.ControlActProcess<T, U> controlActEvent, List<IResultDetail> dtls)
        {
            // Get services
            ITerminologyService term = Context.GetService(typeof(ITerminologyService)) as ITerminologyService;
            ISystemConfigurationService config = Context.GetService(typeof(ISystemConfigurationService)) as ISystemConfigurationService;

            RegistrationEvent retVal = new RegistrationEvent();
            retVal.Context = this.Context;

            // All items here are "completed" so do a proper transform
            retVal.Status = StatusType.Completed;

            // Language code
            if (controlActEvent.LanguageCode == null || controlActEvent.LanguageCode.IsNull)
            {
                dtls.Add(new ResultDetail(ResultDetailType.Warning, this.m_localeService.GetString("MSGE002"), null, null));
                retVal.LanguageCode = config.JurisdictionData.DefaultLanguageCode;
            }
            else
            {
                // By default the language codes used by the SHR is ISO 639-1 
                // However the code used in the messaging is ISO 639-3 so we 
                // have to convert
                var iso6393code = CreateCodeValue(controlActEvent.LanguageCode, dtls);
                if (iso6393code.CodeSystem != config.OidRegistrar.GetOid("ISO639-3").Oid &&
                    iso6393code.CodeSystem != config.OidRegistrar.GetOid("ISO639-1").Oid)
                    dtls.Add(new VocabularyIssueResultDetail(ResultDetailType.Error, this.m_localeService.GetString("MSGE04B"), null));

                // Translate the language code
                if (iso6393code.CodeSystem == config.OidRegistrar.GetOid("ISO639-3").Oid) // we need to translate
                    iso6393code = term.Translate(iso6393code, config.OidRegistrar.GetOid("ISO639-1").Oid);

                if (iso6393code == null)
                    dtls.Add(new VocabularyIssueResultDetail(ResultDetailType.Error, this.m_localeService.GetString("MSGE04C"), null, null));
                else
                    retVal.LanguageCode = iso6393code.Code;
            }

            // Prepare a change summary (ie: the act)
            // All events store a copy of their cact as the "reason" for the change
            ChangeSummary changeSummary = new ChangeSummary();
            changeSummary.ChangeType = CreateCodeValue<String>(controlActEvent.Code, dtls);
            changeSummary.Status = StatusType.Completed;
            changeSummary.Timestamp = DateTime.Now;
            changeSummary.LanguageCode = retVal.LanguageCode;

            if (controlActEvent.EffectiveTime == null || controlActEvent.EffectiveTime.IsNull)
                dtls.Add(new MandatoryElementMissingResultDetail(ResultDetailType.Error, this.m_localeService.GetString("MSGE001"), "//urn:hl7-org:v3#controlActEvent"));
            else
                changeSummary.EffectiveTime = CreateTimestamp(controlActEvent.EffectiveTime, dtls);

            if (controlActEvent.ReasonCode != null)
                foreach(var ce in controlActEvent.ReasonCode)
                    changeSummary.Add(new Reason()
                    {
                        ReasonType = CreateCodeValue<String>(ce, dtls)
                    }, Guid.NewGuid().ToString(), HealthServiceRecordSiteRoleType.ReasonFor, null);
            retVal.Add(changeSummary, "CHANGE", HealthServiceRecordSiteRoleType.ReasonFor | HealthServiceRecordSiteRoleType.OlderVersionOf, null);
            (changeSummary.Site as HealthServiceRecordSite).IsSymbolic = true; // this link adds no real value to the parent's data
            

            // author ( this is optional in IHE)
            if(controlActEvent.Subject == null || controlActEvent.Subject.Count != 1 || controlActEvent.Subject[0].RegistrationEvent == null || controlActEvent.Subject[0].RegistrationEvent.NullFlavor != null)
                ;
            else if (controlActEvent.Subject[0].RegistrationEvent.Author == null || controlActEvent.Subject[0].RegistrationEvent.Author.NullFlavor != null)
                dtls.Add(new ResultDetail(ResultDetailType.Warning, this.m_localeService.GetString("MSGE004"), null, null));
            else
            {
                var autOrPerf  = controlActEvent.Subject[0].RegistrationEvent.Author;
                HealthcareParticipant aut = null;

                if (autOrPerf.Time != null && !autOrPerf.Time.IsNull)
                {
                    var time = autOrPerf.Time.ToBoundIVL();
                    changeSummary.Timestamp  = retVal.Timestamp = (DateTime)(time.Value ?? time.Low);
                    if(controlActEvent.Subject[0].RegistrationEvent.EffectiveTime == null || controlActEvent.Subject[0].RegistrationEvent.EffectiveTime.IsNull || time.SemanticEquals(controlActEvent.Subject[0].RegistrationEvent.EffectiveTime.ToBoundIVL()) == false)
                        dtls.Add(new ValidationResultDetail(ResultDetailType.Error, m_localeService.GetString("MSGE051"), null, null));
                }
                
                // Assigned entity
                if(autOrPerf.AssignedEntity == null || autOrPerf.AssignedEntity.NullFlavor != null)
                    dtls.Add(new ResultDetail(ResultDetailType.Error, this.m_localeService.GetString("MSGE006"), null, null));
                else
                    aut = CreateParticipantComponent(autOrPerf.AssignedEntity, dtls);

                if (aut != null)
                {
                    changeSummary.Add(aut, "AUT", HealthServiceRecordSiteRoleType.AuthorOf, aut.AlternateIdentifiers);
                    retVal.Add(aut.Clone() as IComponent, "AUT".ToString(), HealthServiceRecordSiteRoleType.AuthorOf, aut.AlternateIdentifiers);

                }
                else
                    dtls.Add(new ResultDetail(ResultDetailType.Error, this.m_localeService.GetString("MSGE004"), null, null));
            }

            return retVal;
        }

        /// <summary>
        /// Create a person component
        /// </summary>
        private HealthcareParticipant CreateParticipantComponent(MARC.Everest.RMIM.UV.NE2008.COCT_MT090003UV01.AssignedEntity assignedPerson, List<IResultDetail> dtls)
        {
            HealthcareParticipant retval = new HealthcareParticipant() { Classifier = HealthcareParticipant.HealthcareParticipantType.Person }

            // Identifiers
            if (assignedPerson.Id == null || assignedPerson.Id.IsNull || assignedPerson.Id.IsEmpty)
                dtls.Add(new MandatoryElementMissingResultDetail(ResultDetailType.Error, this.m_localeService.GetString("MSGE02F"), null));
            else
                retval.AlternateIdentifiers.AddRange(CreateDomainIdentifierList(assignedPerson.Id));
            
            // Type
            if(assignedPerson.Code != null && !assignedPerson.Code.IsNull)
                retval.Type = CreateCodeValue(assignedPerson.Code, dtls);

            // Address
            if(assignedPerson.Addr != null && !assignedPerson.Addr.IsEmpty)
                retval.PrimaryAddress = CreateAddressSet(assignedPerson.Addr.Find(o=>o.Use.Contains(PostalAddressUse.WorkPlace)) ?? assignedPerson.Addr[0], dtls);

            // Telecom
            if(assignedPerson.Telecom != null && !assignedPerson.Telecom.IsEmpty)
                foreach(var tel in assignedPerson.Telecom)
                    retval.TelecomAddresses.Add(new SVC.Core.DataTypes.TelecommunicationsAddress() {
                        Value = tel.Value, 
                        Use = Util.ToWireFormat(tel.Use)
                    });

            // Assigned person
            if(assignedPerson.AssignedPrincipalChoiceList == null || assignedPerson.AssignedPrincipalChoiceList.NullFlavor != null)
                dtls.Add(new MandatoryElementMissingResultDetail(ResultDetailType.Error, this.m_localeService.GetString("MSGE030"), null));
            else
            {
                if(assignedPerson.AssignedPrincipalChoiceList is MARC.Everest.RMIM.UV.NE2008.COCT_MT090103UV01.Person)
                {
                    var psn = assignedPerson.AssignedPrincipalChoiceList as MARC.Everest.RMIM.UV.NE2008.COCT_MT090103UV01.Person;
                    retval.LegalName = psn.Name != null && !psn.Name.IsNull ? CreateNameSet(psn.Name.Find(o=>o.Use.Contains(EntityNameUse.Legal)) ?? psn.Name[0], dtls) : null;
                }
                else if(assignedPerson.AssignedPrincipalChoiceList is MARC.Everest.RMIM.UV.NE2008.COCT_MT090303UV01.Device)
                {
                    var dev = assignedPerson.AssignedPrincipalChoiceList as MARC.Everest.RMIM.UV.NE2008.COCT_MT090303UV01.Device;
                    retval.LegalName = dev.SoftwareName != null && !dev.SoftwareName.IsNull ? new NameSet() { Parts = new List<NamePart>() { new NamePart() { Value = dev.SoftwareName, Type = NamePart.NamePartType.Given }}} : null;
                    retval.Classifier = HealthcareParticipant.HealthcareParticipantType.Organization | HealthcareParticipant.HealthcareParticipantType.Person;
                }
                else
                    dtls.Add(new NotSupportedChoiceResultDetail(ResultDetailType.Error, m_localeService.GetString("MSGE051"), null));
            }

            return retval;
        }

        /// <summary>
        /// Create components for the message IHE ITI44
        /// </summary>
        internal RegistrationEvent CreateComponents(MARC.Everest.RMIM.UV.NE2008.MFMI_MT700701UV01.ControlActProcess<MARC.Everest.RMIM.UV.NE2008.PRPA_MT201301UV02.Patient, object> controlActProcess, List<IResultDetail> dtls)
        {
            ITerminologyService termSvc = Context.GetService(typeof(ITerminologyService)) as ITerminologyService;
            ISystemConfigurationService config = Context.GetService(typeof(ISystemConfigurationService)) as ISystemConfigurationService;

            // Create return value
            RegistrationEvent retVal = CreateComponents<MARC.Everest.RMIM.UV.NE2008.PRPA_MT201301UV02.Patient, object>(controlActProcess, dtls);

            // Very important, if there is more than one subject then we have a problem
            if (controlActProcess.Subject.Count != 1)
            {
                dtls.Add(new InsufficientRepetionsResultDetail(ResultDetailType.Error, this.m_localeService.GetString("MSGE04F"), "//urn:hl7-org:v3#controlActProcess/urn:hl7-org:v3#subject"));
                return null;
            }

            var subject = controlActProcess.Subject[0].RegistrationEvent;

            retVal.EventClassifier = RegistrationEventType.Register;
            retVal.EventType = new CodeValue("REG");
            retVal.Status =subject.StatusCode == null || subject.StatusCode.IsNull ? StatusType.Active : ConvertStatusCode(subject.StatusCode, dtls);

            // Control act event code
            if (!controlActProcess.Code.Code.Equals(PRPA_IN201301UV02.GetTriggerEvent().Code))
            {
                dtls.Add(new ResultDetail(ResultDetailType.Error, this.m_localeService.GetString("MSGE00C"), null, null));
                return null;
            }

            if (retVal == null) return null;

            // Create the subject
            Person subjectOf = new Person();

            // Validate
            if (subject.NullFlavor != null)
                dtls.Add(new MandatoryElementMissingResultDetail(ResultDetailType.Error, this.m_localeService.GetString("MSGE003"), null));

            // Subject ID
            if(subject.Id != null && subject.Id.Count > 0)
                retVal.Add(new ExtendedAttribute() {
                    PropertyPath = "Id", 
                    Value = CreateDomainIdentifierList(subject.Id), 
                    Name = "RegistrationEventAltId"
                });

            // Effective time of the registration event = authored time
            if(subject.EffectiveTime != null && !subject.EffectiveTime.IsNull)
            {
                var ivl = subject.EffectiveTime.ToBoundIVL();
                retVal.Timestamp  = (DateTime)(ivl.Value ?? ivl.Low);
                if(subject.Author == null || subject.Author.Time == null || subject.Author.Time.IsNull || subject.Author.Time.ToBoundIVL().SemanticEquals(ivl) == false)
                        dtls.Add(new ValidationResultDetail(ResultDetailType.Error, m_localeService.GetString("MSGE051"), null, null));

            }

            // Custodian of the record
            if (subject.Custodian == null || subject.Custodian.NullFlavor != null ||
                subject.Custodian.AssignedEntity == null || subject.Custodian.AssignedEntity.NullFlavor != null)
                dtls.Add(new MandatoryElementMissingResultDetail(ResultDetailType.Error, this.m_localeService.GetString("MSGE00B"), null));
            else
            {
                var cstdn = CreateRepositoryDevice(subject.Custodian.AssignedEntity, dtls);
                if (cstdn != null)
                    retVal.Add(CreateRepositoryDevice(subject.Custodian.AssignedEntity, dtls), "CST",
                        HealthServiceRecordSiteRoleType.PlaceOfRecord | HealthServiceRecordSiteRoleType.ResponsibleFor,
                            CreateDomainIdentifierList(subject.Custodian.AssignedEntity.Id)
                        );
            }

            // Replacement of?
            foreach (var rplc in subject.ReplacementOf)
                if (rplc.NullFlavor == null &&
                    rplc.PriorRegistration != null && rplc.PriorRegistration.NullFlavor == null)
                {

                    if (rplc.PriorRegistration.Subject1 == null || rplc.PriorRegistration.Subject1.NullFlavor != null ||
                        rplc.PriorRegistration.Subject1.PriorRegisteredRole == null || rplc.PriorRegistration.Subject1.PriorRegisteredRole.NullFlavor != null ||
                        rplc.PriorRegistration.Subject1.PriorRegisteredRole.Id == null || rplc.PriorRegistration.Subject1.PriorRegisteredRole.Id.IsEmpty ||
                        rplc.PriorRegistration.Subject1.PriorRegisteredRole.Id.IsNull)
                        dtls.Add(new MandatoryElementMissingResultDetail(ResultDetailType.Error, m_localeService.GetString("MSGE050"), "//urn:hl7-org:v3#priorRegisteredRole"));
                    else
                        subjectOf.Add(new PersonRegistrationRef()
                        {
                            AlternateIdentifiers = CreateDomainIdentifierList(rplc.PriorRegistration.Subject1.PriorRegisteredRole.Id)
                        }, Guid.NewGuid().ToString(), HealthServiceRecordSiteRoleType.ReplacementOf, null);
                            
                }
        
            // Process additional data
            var regRole = subject.Subject1.registeredRole;

            // Any alternate ids?
            if (regRole.Id != null && !regRole.Id.IsNull)
            {
                subjectOf.AlternateIdentifiers = new List<DomainIdentifier>();
                foreach (var ii in regRole.Id)
                    if (!ii.IsNull)
                        subjectOf.AlternateIdentifiers.Add(CreateDomainIdentifier(ii));

            }

            // Status code
            if (regRole.StatusCode != null && !regRole.StatusCode.IsNull)
                subjectOf.Status = ConvertStatusCode(regRole.StatusCode, dtls);

            // Effective time
            if (subjectOf.Status == StatusType.Active || regRole.EffectiveTime == null || regRole.EffectiveTime.IsNull)
            {
                dtls.Add(new RequiredElementMissingResultDetail(ResultDetailType.Warning, this.m_localeService.GetString("MSGW005"), null));
                retVal.EffectiveTime = CreateTimestamp(new IVL<TS>(DateTime.Now, new TS() { NullFlavor = NullFlavor.NotApplicable }), dtls);
            }
            else
                retVal.EffectiveTime = CreateTimestamp(regRole.EffectiveTime, dtls);

            // Masking indicator
            if (regRole.ConfidentialityCode != null && !regRole.ConfidentialityCode.IsNull)
                foreach(var msk in regRole.ConfidentialityCode)
                    subjectOf.Add(new MaskingIndicator()
                    {
                        MaskingCode = CreateCodeValue(msk, dtls)
                    }, Guid.NewGuid().ToString(), HealthServiceRecordSiteRoleType.FilterOf, null);

            // Identified entity check
            var ident = regRole.PatientEntityChoiceSubject as MARC.Everest.RMIM.UV.NE2008.PRPA_MT201310UV02.Person;
            if (ident == null || ident.NullFlavor != null)
            {
                dtls.Add(new MandatoryElementMissingResultDetail(ResultDetailType.Error, this.m_localeService.GetString("MSGE012"), null));
                return null;
            }

            // Names
            if (ident.Name != null)
            {
                subjectOf.Names = new List<SVC.Core.DataTypes.NameSet>(ident.Name.Count);
                foreach (var nam in ident.Name)
                    if (!nam.IsNull)
                        subjectOf.Names.Add(CreateNameSet(nam, dtls));
            }

            // Telecoms
            if (ident.Telecom != null)
            {
                subjectOf.TelecomAddresses = new List<SVC.Core.DataTypes.TelecommunicationsAddress>(ident.Telecom.Count);
                foreach (var tel in ident.Telecom)
                {
                    if (tel.IsNull) continue;

                    subjectOf.TelecomAddresses.Add(new SVC.Core.DataTypes.TelecommunicationsAddress()
                    {
                        Use = Util.ToWireFormat(tel.Use),
                        Value = tel.Value
                    });

                    // Store usable period as an extension as it is not storable here
                    if (tel.UseablePeriod != null && !tel.UseablePeriod.IsNull)
                    {
                        subjectOf.Add(new ExtendedAttribute()
                        {
                            PropertyPath = String.Format("TelecomAddresses[{0}]", tel.Value),
                            Value = tel.UseablePeriod.Hull,
                            Name = "UsablePeriod"
                        });
                    }
                }
            }

            // Gender
            if (ident.AdministrativeGenderCode != null && !ident.AdministrativeGenderCode.IsNull)
                subjectOf.GenderCode = Util.ToWireFormat(ident.AdministrativeGenderCode);

            // Birth
            if (ident.BirthTime != null && !ident.BirthTime.IsNull)
                subjectOf.BirthTime = CreateTimestamp(ident.BirthTime, dtls);

            // Deceased
            if (ident.DeceasedInd != null && !ident.DeceasedInd.IsNull)
                dtls.Add(new NotImplementedElementResultDetail(ResultDetailType.Warning, "DeceasedInd", this.m_localeService.GetString("MSGW006"), null));
            if (ident.DeceasedTime != null && !ident.DeceasedTime.IsNull)
                subjectOf.DeceasedTime = CreateTimestamp(ident.DeceasedTime, dtls);

            // Multiple Birth
            if (ident.MultipleBirthInd != null && !ident.MultipleBirthInd.IsNull)
                dtls.Add(new NotImplementedElementResultDetail(ResultDetailType.Warning, "DeceasedInd", this.m_localeService.GetString("MSGW007"), null));
            if (ident.MultipleBirthOrderNumber != null && !ident.MultipleBirthOrderNumber.IsNull)
                subjectOf.BirthOrder = ident.MultipleBirthOrderNumber;

            // Address(es)
            if (ident.Addr != null)
            {
                subjectOf.Addresses = new List<SVC.Core.DataTypes.AddressSet>(ident.Addr.Count);
                foreach (var addr in ident.Addr)
                    if (!addr.IsNull)
                        subjectOf.Addresses.Add(CreateAddressSet(addr, dtls));
            }

            // As other identifiers
            if (ident.AsOtherIDs != null)
            {
                subjectOf.OtherIdentifiers = new List<KeyValuePair<CodeValue, DomainIdentifier>>(ident.AsOtherIDs.Count);
                foreach (var id in ident.AsOtherIDs)
                    if (id.NullFlavor == null)
                    {

                        // Ignore
                        if (id.Id == null || id.Id.IsNull || id.Id.IsEmpty)
                            continue;

                        // Other identifiers 
                        var priId = id.Id[0];
                        subjectOf.OtherIdentifiers.Add(new KeyValuePair<CodeValue, DomainIdentifier>(
                            null,
                            CreateDomainIdentifier(priId)
                         ));

                        // Extra "other" identifiers are extensions
                        for (int i = 1; i < id.Id.Count; i++)
                            subjectOf.Add(new ExtendedAttribute()
                            {
                                PropertyPath = String.Format("OtherIdentifiers[{0}{1}]", priId.Root, priId.Extension),
                                Value = id.Id[i],
                                Name = "AssigningIdOrganizationExtraId"
                            });

                        // Extra scoping org data
                        if (id.ScopingOrganization != null && id.ScopingOrganization.NullFlavor == null)
                        {
                            
                            // Other identifier assigning organization ext
                            if (id.ScopingOrganization.Id != null && !id.ScopingOrganization.Id.IsNull)
                                foreach (var othScopeId in id.ScopingOrganization.Id)
                                {
                                    subjectOf.Add(new ExtendedAttribute()
                                    {
                                        PropertyPath = String.Format("OtherIdentifiers[{0}{1}]", priId.Root, priId.Extension),
                                        Value = CreateDomainIdentifier(othScopeId),
                                        Name = "AssigningIdOrganizationId"
                                    });
                                }
                            // Other identifier assigning organization name
                            if (id.ScopingOrganization.Name != null && !id.ScopingOrganization.Name.IsNull)
                                foreach(var othScopeName in id.ScopingOrganization.Name)
                                    subjectOf.Add(new ExtendedAttribute()
                                    {
                                        PropertyPath = String.Format("OtherIdentifiers[{0}{1}]", priId.Root, priId.Extension),
                                        Value = othScopeName.ToString(),
                                        Name = "AssigningIdOrganizationName"
                                    });
                            if(id.ScopingOrganization.Code != null && !id.ScopingOrganization.Code.IsNull)
                                subjectOf.Add(new ExtendedAttribute()
                                {
                                    PropertyPath = String.Format("OtherIdentifiers[{0}{1}]", priId.Root, priId.Extension),
                                    Value = CreateCodeValue(id.ScopingOrganization.Code, dtls),
                                    Name = "AssigningIdOrganizationCode"
                                });

                        }
                    }
            }

            // Languages
            if (ident.LanguageCommunication != null)
            {
                subjectOf.Language = new List<PersonLanguage>(ident.LanguageCommunication.Count);
                foreach (var lang in ident.LanguageCommunication)
                {
                    if (lang == null || lang.NullFlavor != null) continue;

                    PersonLanguage pl = new PersonLanguage();

                    CodeValue languageCode = CreateCodeValue(lang.LanguageCode, dtls);
                    // Default ISO 639-3
                    languageCode.CodeSystem = languageCode.CodeSystem ?? config.OidRegistrar.GetOid("ISO639-3").Oid;

                    // Validate the language code
                    if (languageCode.CodeSystem != config.OidRegistrar.GetOid("ISO639-3").Oid &&
                        languageCode.CodeSystem != config.OidRegistrar.GetOid("ISO639-1").Oid)
                        dtls.Add(new VocabularyIssueResultDetail(ResultDetailType.Error, this.m_localeService.GetString("MSGE04B"), null));

                    // Translate the language code
                    if (languageCode.CodeSystem == config.OidRegistrar.GetOid("ISO639-3").Oid) // we need to translate
                        languageCode = termSvc.Translate(languageCode, config.OidRegistrar.GetOid("ISO639-1").Oid);

                    if (languageCode == null)
                        dtls.Add(new VocabularyIssueResultDetail(ResultDetailType.Error, this.m_localeService.GetString("MSGE04C"), null, null));
                    else
                        pl.Language = languageCode.Code;

                    // Preferred? 
                    if ((bool)lang.PreferenceInd)
                        pl.Type = LanguageType.Fluency;
                    else
                        pl.Type = LanguageType.WrittenAndSpoken;

                    // Add
                    subjectOf.Language.Add(pl);
                }
            }

            // Personal relationship
            if (ident.PersonalRelationship != null)
                foreach (var psn in ident.PersonalRelationship)
                    if (psn.NullFlavor == null && psn.RelationshipHolder1 != null &&
                        psn.RelationshipHolder1.NullFlavor == null)
                        subjectOf.Add(CreatePersonalRelationship(psn, dtls), Guid.NewGuid().ToString(), HealthServiceRecordSiteRoleType.RepresentitiveOf, null);

            // VIP Code
            if (regRole.VeryImportantPersonCode != null && !regRole.VeryImportantPersonCode.IsNull)
                subjectOf.VipCode = CreateCodeValue(regRole.VeryImportantPersonCode, dtls);

            // Birthplace
            if (ident.BirthPlace != null && ident.BirthPlace.NullFlavor == null &&
                ident.BirthPlace.Birthplace != null && ident.BirthPlace.Birthplace.NullFlavor == null)
                subjectOf.BirthPlace = CreateBirthplace(ident.BirthPlace.Birthplace, dtls);

            // Race Codes
            if (ident.RaceCode != null && !ident.RaceCode.IsNull)
            {
                subjectOf.Race = new List<CodeValue>(ident.RaceCode.Count);
                foreach (var rc in ident.RaceCode)
                    subjectOf.Race.Add(CreateCodeValue(rc, dtls));
            }

            // Ethnicity Codes
            // Didn't actually have a place for this so this will be an extension
            if (ident.EthnicGroupCode != null && !ident.EthnicGroupCode.IsNull)
                foreach (var eth in ident.EthnicGroupCode)
                    subjectOf.Add(new ExtendedAttribute()
                    {
                        Name = "EthnicGroupCode",
                        PropertyPath = "",
                        Value = CreateCodeValue(eth, dtls)
                    });

            
            // Marital Status Code
            if (ident.MaritalStatusCode != null && !ident.MaritalStatusCode.IsNull)
                subjectOf.MaritalStatus = CreateCodeValue(ident.MaritalStatusCode, dtls);

            // Religion code
            if (ident.ReligiousAffiliationCode != null && !ident.ReligiousAffiliationCode.IsNull)
                subjectOf.ReligionCode = CreateCodeValue(ident.ReligiousAffiliationCode, dtls);
            
            // Citizenship Code
            if (ident.AsCitizen.Count > 0)
            {
                subjectOf.Citizenship = new List<Citizenship>(ident.AsCitizen.Count);
                foreach (var cit in ident.AsCitizen)
                {
                    if (cit.NullFlavor != null) continue;

                    Citizenship citizenship = new Citizenship(); // canonical 
                    if (cit.PoliticalNation != null && cit.PoliticalNation.NullFlavor == null &&
                        cit.PoliticalNation.Code != null && !cit.PoliticalNation.Code.IsNull)
                    {

                        // The internal canonical form specifies ISO3166 codes to be used for storage of nation codes
                        var iso3166code = CreateCodeValue(cit.PoliticalNation.Code, dtls);
                        if (iso3166code.CodeSystem != config.OidRegistrar.GetOid("ISO3166-1").Oid &&
                            iso3166code.CodeSystem != config.OidRegistrar.GetOid("ISO3166-2").Oid)
                            dtls.Add(new VocabularyIssueResultDetail(ResultDetailType.Error, this.m_localeService.GetString("MSGE057"), null));

                        // Translate the language code
                        if (iso3166code.CodeSystem == config.OidRegistrar.GetOid("ISO3166-2").Oid) // we need to translate
                            iso3166code = termSvc.Translate(iso3166code, config.OidRegistrar.GetOid("ISO3166-1").Oid);

                        if (iso3166code == null)
                            dtls.Add(new VocabularyIssueResultDetail(ResultDetailType.Error, this.m_localeService.GetString("MSGE058"), null, null));
                        else
                            citizenship.CountryCode = iso3166code.Code;

                        // Name of the country
                        if(cit.PoliticalNation.Name != null && !cit.PoliticalNation.Name.IsNull && cit.PoliticalNation.Name.Part.Count > 0)
                            citizenship.CountryName = cit.PoliticalNation.Name.Part[0].Value;

                    }
                    else
                        dtls.Add(new MandatoryElementMissingResultDetail(ResultDetailType.Error, m_localeService.GetString("MSGE056"), null));

                    // Get other details
                    // Effective time of the citizenship
                    if (cit.EffectiveTime != null && !cit.EffectiveTime.IsNull)
                        citizenship.EffectiveTime = CreateTimestamp(cit.EffectiveTime, dtls);

                    // Identifiers of the citizen in the role
                    if (cit.Id != null && !cit.Id.IsNull)
                        subjectOf.Add(new ExtendedAttribute()
                        {
                            Name = "CitizenshipIds",
                            PropertyPath = String.Format("Citizenship[{0}]", citizenship.CountryCode),
                            Value = CreateDomainIdentifierList(cit.Id)
                        });

                    subjectOf.Citizenship.Add(citizenship);
                }
            }

            // Employment Code
            if (ident.AsEmployee.Count > 0)
            {
                subjectOf.Employment = new List<Employment>(ident.AsEmployee.Count);
                foreach (var emp in ident.AsEmployee)
                {
                    if (emp.NullFlavor != null) continue;

                    Employment employment = new Employment();

                    // Occupation code
                    if (emp.OccupationCode != null && !emp.OccupationCode.IsNull)
                        employment.Occupation = CreateCodeValue(emp.OccupationCode, dtls);
                    
                    // efft time
                    if (emp.EffectiveTime != null && !emp.EffectiveTime.IsNull)
                        employment.EffectiveTime = CreateTimestamp(emp.EffectiveTime, dtls);

                    // status
                    if (emp.StatusCode != null && !emp.StatusCode.IsNull)
                        employment.Status = ConvertStatusCode(Util.Convert<RoleStatus1>(Util.ToWireFormat(emp.StatusCode)), dtls);

                    subjectOf.Employment.Add(employment);
                }
            }

            retVal.Add(subjectOf, "SUBJ", HealthServiceRecordSiteRoleType.SubjectOf,
                subjectOf.AlternateIdentifiers);

            // Error?
            if (dtls.Exists(o => o.Type == ResultDetailType.Error))
                retVal = null;

            return retVal;
        }

        /// <summary>
        /// Create a birthplace
        /// </summary>
        private ServiceDeliveryLocation CreateBirthplace(MARC.Everest.RMIM.UV.NE2008.COCT_MT710007UV.Place place, List<IResultDetail> dtls)
        {
            var retVal = new ServiceDeliveryLocation();

            // Place id
            if (place.Id != null && !place.Id.IsNull)
                retVal.AlternateIdentifiers = CreateDomainIdentifierList(place.Id);

            // Address of the place
            if (place.Addr != null && !place.Addr.IsNull)
                retVal.Address = CreateAddressSet(place.Addr, dtls);

            // Name of the place
            if (place.Name != null && !place.Name.IsNull && !place.Name.IsNull)
                retVal.Name = place.Name[0].ToString();

            // Place type
            if (place.Code != null && !place.Code.IsNull)
                retVal.LocationType = CreateCodeValue(place.Code, dtls);

            return retVal;
        }


        /// <summary>
        /// Convert an act status code
        /// </summary>
        private StatusType ConvertStatusCode(CS<ActStatus> statusCode,List<IResultDetail> dtls)
        {
            // Determine if a valid code was selected from the ActStatus domain
            if (statusCode.Code.IsAlternateCodeSpecified)
            {
                dtls.Add(new VocabularyIssueResultDetail(ResultDetailType.Error, m_localeService.GetString("MSGE053"), null, null));
                return StatusType.Unknown;
            }

            switch ((ActStatus)statusCode)
            {
                case ActStatus.Aborted:
                    return StatusType.Aborted;
                case ActStatus.Active:
                    return StatusType.Active;
                case ActStatus.Cancelled:
                    return StatusType.Cancelled;
                case ActStatus.Completed:
                    return StatusType.Completed;
                case ActStatus.New:
                    return StatusType.New;
                case ActStatus.Nullified:
                    return StatusType.Nullified;
                case ActStatus.Obsolete:
                    return StatusType.Obsolete;
                case ActStatus.Suspended:
                case ActStatus.Normal:
                case ActStatus.Held:
                    dtls.Add(new VocabularyIssueResultDetail(ResultDetailType.Error, m_localeService.GetString("MSGE054"), null, null));
                    return StatusType.Unknown;
                default:
                    dtls.Add(new VocabularyIssueResultDetail(ResultDetailType.Error, m_localeService.GetString("MSGE053"), null, null));
                    return StatusType.Unknown;
            }
        } 

        /// <summary>
        /// Create a repository device
        /// </summary>
        private IComponent CreateRepositoryDevice(MARC.Everest.RMIM.UV.NE2008.COCT_MT090003UV01.AssignedEntity assignedEntity, List<IResultDetail> dtls)
        {
            if (assignedEntity.AssignedPrincipalChoiceList is MARC.Everest.RMIM.UV.NE2008.COCT_MT090303UV01.Device)
            {
                var dev = assignedEntity.AssignedPrincipalChoiceList as MARC.Everest.RMIM.UV.NE2008.COCT_MT090303UV01.Device;
                // Validate
                if(dev.NullFlavor != null ||
                    dev.Id == null ||
                    dev.Id.IsNull ||
                    dev.Id.IsEmpty)
                    dtls.Add(new MandatoryElementMissingResultDetail(ResultDetailType.Error, m_localeService.GetString("MSGE00D"), null));

                // Create the repo dev
                var retVal = new RepositoryDevice()
                {
                    AlternateIdentifier = CreateDomainIdentifier(dev.Id[0]),
                    Name = dev.SoftwareName
                };

                return retVal;
            }
            else if (assignedEntity.AssignedPrincipalChoiceList is MARC.Everest.RMIM.UV.NE2008.COCT_MT090203UV01.Organization)
            {
                // Create org
                var org = assignedEntity.AssignedPrincipalChoiceList as MARC.Everest.RMIM.UV.NE2008.COCT_MT090203UV01.Organization;

                if(assignedEntity.NullFlavor != null ||
                    assignedEntity.Id == null ||
                    assignedEntity.Id.IsNull ||
                    assignedEntity.Id.IsEmpty)
                    dtls.Add(new MandatoryElementMissingResultDetail(ResultDetailType.Error, m_localeService.GetString("MSGE00D"), null));

                HealthcareParticipant ptcpt = new HealthcareParticipant() { Classifier = HealthcareParticipant.HealthcareParticipantType.Organization };
                ptcpt.AlternateIdentifiers = CreateDomainIdentifierList(assignedEntity.Id);
                ptcpt.LegalName = org.Name != null && !org.Name.IsNull ? CreateNameSet(org.Name.Find(o => o.Use.Contains(EntityNameUse.Legal)) ?? org.Name[0], dtls) : null;
                ptcpt.PrimaryAddress = assignedEntity.Addr != null && !assignedEntity.Addr.IsNull ? CreateAddressSet(assignedEntity.Addr.Find(o => o.Use.Contains(PostalAddressUse.Direct)) ?? assignedEntity.Addr[0], dtls) : null;

                // Telecom addresses
                if(assignedEntity.Telecom != null && assignedEntity.Telecom.IsNull)
                {
                    ptcpt.TelecomAddresses = new List<TelecommunicationsAddress>();
                    foreach(var tel in assignedEntity.Telecom)
                        ptcpt.TelecomAddresses.Add(new TelecommunicationsAddress() {
                            Value= tel.Value,
                            Use = Util.ToWireFormat(tel.Use)
                        });
                };

                return ptcpt;
            }
            else
            {
                dtls.Add(new NotSupportedChoiceResultDetail(ResultDetailType.Error, m_localeService.GetString("MSGE055"), null, null));
                return null;
            }
        }

        /// <summary>
        /// Create a personal relationship
        /// </summary>
        private PersonalRelationship CreatePersonalRelationship(MARC.Everest.RMIM.UV.NE2008.PRPA_MT201303UV02.PersonalRelationship psn, List<IResultDetail> dtls)
        {
            
        }

        /// <summary>
        /// Convert status code
        /// </summary>
        private StatusType ConvertStatusCode(CS<RoleStatus1> status, List<IResultDetail> dtls)
        {
            if (status.Code.IsAlternateCodeSpecified)
            {
                dtls.Add(new VocabularyIssueResultDetail(ResultDetailType.Error, m_localeService.GetString("MSGE010"), null, null));
                return StatusType.Unknown;
            }

            // Status
            switch ((RoleStatus1)status)
            {
                case RoleStatus1.Active:
                    return StatusType.Active;
                case RoleStatus1.Cancelled:
                    return StatusType.Cancelled;
                case RoleStatus1.Nullified:
                    return StatusType.Nullified;
                case RoleStatus1.Pending:
                    return StatusType.New;
                case RoleStatus1.Suspended:
                    return StatusType.Aborted;
                case RoleStatus1.Terminated:
                    return StatusType.Obsolete;
                case RoleStatus1.Normal:
                    dtls.Add(new VocabularyIssueResultDetail(ResultDetailType.Error, m_localeService.GetString("MSGE011"), null, null));
                    return StatusType.Unknown;
                default:
                    dtls.Add(new VocabularyIssueResultDetail(ResultDetailType.Error, m_localeService.GetString("MSGE010"), null, null));
                    return StatusType.Unknown;
            }
        }
    }
}