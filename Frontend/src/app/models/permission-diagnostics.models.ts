export type DiagnosticStatus = 'Healthy' | 'Warning' | 'Critical' | 'Unknown' | 'NotApplicable';
export type PermissionDiagnosticScope = 'ConfiguredFeatures' | 'AllChannels' | 'Full';
export interface PermissionTraceStep { source: string; allowed: boolean | null; detailKey: string; }
export interface PermissionTrace { permission: string; effective: boolean; steps: PermissionTraceStep[]; decisiveSource: string | null; }
export interface PermissionCheck { checkKey: string; status: DiagnosticStatus; featureKey: string; resourceId: string | null; resourceType: string; resourceName: string; requiredPermissions: string[]; presentPermissions: string[]; missingPermissions: string[]; permissionTraces: PermissionTrace[]; titleKey: string; descriptionKey: string; remediationKey: string; parameters: Record<string, string>; impact: string; }
export interface FeatureDiagnostic { featureKey: string; status: DiagnosticStatus; enabled: boolean; checks: PermissionCheck[]; }
export interface RoleDiagnostic { roleId: string; roleName: string; featureKey: string; status: DiagnosticStatus; reasonKey: string; remediationKey: string; }
export interface ChannelDiagnostic { channelId: string; channelName: string; channelType: string; categoryId: string | null; categoryName: string | null; features: string[]; checks: PermissionCheck[]; }
export interface PermissionDiagnosticReport { runId: string; guildId: string; generatedAt: string; overallStatus: DiagnosticStatus; completeness: string; globalChecks: PermissionCheck[]; featureChecks: FeatureDiagnostic[]; roleChecks: RoleDiagnostic[]; channelChecks: ChannelDiagnostic[]; statistics: { featuresChecked: number; channelsChecked: number; critical: number; warnings: number; unknown: number }; }
