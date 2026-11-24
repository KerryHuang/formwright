import type { RouteRecordRaw } from 'vue-router';

export const routes: RouteRecordRaw[] = [
  { path: '/login', component: () => import('src/pages/auth/LoginPage.vue'), meta: { public: true } },
  { path: '/service-unavailable', component: () => import('src/pages/auth/ServiceUnavailablePage.vue'), meta: { public: true } },
  { path: '/change-password', component: () => import('src/pages/auth/ChangePasswordPage.vue') },
  // 強制綁定雙因素（帳號安全設計 §8.7、§12）：和改密碼頁一樣不套主版面，只能綁定或登出
  { path: '/account/two-factor-setup', component: () => import('src/pages/auth/TwoFactorSetupPage.vue') },
  // 驗證 Email 的連結（帳號安全設計 §12）：登入與否都能開，所以 anyone 讓守衛不把已登入的人導回首頁
  { path: '/verify-email', component: () => import('src/pages/auth/VerifyEmailPage.vue'), meta: { public: true, anyone: true } },
  // 寄信重設密碼（帳號安全設計 §12）：申請頁只給未登入的人；重設連結登入與否都能開（同 /verify-email）
  { path: '/forgot-password', component: () => import('src/pages/auth/ForgotPasswordPage.vue'), meta: { public: true } },
  { path: '/reset-password', component: () => import('src/pages/auth/ResetPasswordPage.vue'), meta: { public: true, anyone: true } },
  {
    path: '/',
    component: () => import('src/layouts/MainLayout.vue'),
    children: [
      { path: '', component: () => import('src/pages/HomePage.vue') },
      { path: 'dynamic/:moduleKey', component: () => import('src/pages/dynamic/ListPage.vue') },
      { path: 'dynamic/:moduleKey/new', component: () => import('src/pages/dynamic/FormPage.vue') },
      { path: 'dynamic/:moduleKey/:id(\\d+)', component: () => import('src/pages/dynamic/DetailPage.vue') },
      { path: 'dynamic/:moduleKey/:id(\\d+)/edit', component: () => import('src/pages/dynamic/FormPage.vue') },
      { path: 'account/ai-tokens', component: () => import('src/pages/account/AiTokensPage.vue') },
      { path: 'account/security', component: () => import('src/pages/account/AccountSecurityPage.vue') },
      { path: 'tasks', component: () => import('src/pages/tasks/MyTasksPage.vue') },
      { path: 'approvals', component: () => import('src/pages/approvals/ApprovalCenterPage.vue') },
      { path: 'reports', component: () => import('src/pages/reports/ReportListPage.vue') },
      { path: 'reports/new', component: () => import('src/pages/reports/ReportDesignerPage.vue') },
      { path: 'reports/:id(\\d+)', component: () => import('src/pages/reports/ReportViewPage.vue') },
      { path: 'reports/:id(\\d+)/edit', component: () => import('src/pages/reports/ReportDesignerPage.vue') },
      { path: 'dashboards/:id(\\d+)?', component: () => import('src/pages/dashboards/DashboardPage.vue') },
      { path: 'system/dashboards', component: () => import('src/pages/system/DashboardsAdminPage.vue') },
      { path: 'system/dynamic-modules', component: () => import('src/pages/system/dynamic-modules/ModuleListPage.vue') },
      // 流程圖設計頁（fg3c）：新流程用 `_new`，底線不在流程代碼規則內，所以代碼叫 `new` 的流程照常打得開。側邊選單預設收起（collapseDrawer）。
      {
        path: 'system/dynamic-modules/:moduleKey/approval-flows/_new', name: 'flow-designer-new', meta: { collapseDrawer: true },
        component: () => import('src/pages/system/dynamic-modules/FlowDesignerPage.vue'),
      },
      {
        path: 'system/dynamic-modules/:moduleKey/approval-flows/:flowKey', name: 'flow-designer', meta: { collapseDrawer: true },
        component: () => import('src/pages/system/dynamic-modules/FlowDesignerPage.vue'),
      },
      { path: 'system/dynamic-modules/:moduleKey', component: () => import('src/pages/system/dynamic-modules/ModuleEditorPage.vue') },
      { path: 'system/conversions', component: () => import('src/pages/system/ConversionsPage.vue') },
      { path: 'system/users', component: () => import('src/pages/system/UsersPage.vue') },
      { path: 'system/groups', component: () => import('src/pages/system/GroupsPage.vue') },
      { path: 'system/organization', component: () => import('src/pages/system/OrganizationPage.vue') },
      { path: 'system/appearance', component: () => import('src/pages/system/AppearancePage.vue') },
      { path: 'system/policies', component: () => import('src/pages/system/PoliciesPage.vue') },
      { path: 'system/locales', component: () => import('src/pages/system/LocalesPage.vue') },
      { path: 'system/ops', component: () => import('src/pages/system/OpsPage.vue') },
      { path: 'system/audit', component: () => import('src/pages/system/AuditPage.vue') },
      { path: 'system/ai-access', component: () => import('src/pages/system/AiAccessPage.vue') },
      { path: 'system/mail', component: () => import('src/pages/system/MailSettingsPage.vue') },
      { path: 'system/apps', component: () => import('src/pages/system/AppsPage.vue') },
      { path: 'system/apps/:key/upgrade', component: () => import('src/pages/system/AppUpgradePage.vue') },
      { path: 'system/demo-data', component: () => import('src/pages/system/DemoDataPage.vue') },
    ],
  },
];
