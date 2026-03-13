import { Link, useLocation, Outlet } from 'react-router-dom';
import { useAuth } from '../../contexts/AuthContext';
import {
  LayoutDashboard, FileText, Upload, Settings, Users,
  ClipboardList, LogOut, X, Cpu, FlaskConical, Building2, Trash2, MoreHorizontal,
} from 'lucide-react';
import { useState, useRef, useEffect } from 'react';

const navItems = [
  { to: '/dashboard',          label: 'Dashboard', icon: LayoutDashboard },
  { to: '/documents',          label: 'Documents',  icon: FileText },
  { to: '/documents/upload',   label: 'Upload',     icon: Upload },
];

const adminItems = [
  { to: '/admin/vendors',     label: 'Vendors',      icon: Building2 },
  { to: '/admin/users',       label: 'Users',        icon: Users },
  { to: '/admin/config',      label: 'Field Config', icon: Settings },
  { to: '/admin/audit',       label: 'Audit Log',    icon: ClipboardList },
  { to: '/admin/erp-test',    label: 'ERP Test',     icon: FlaskConical },
  { to: '/admin/rubbish-bin', label: 'Rubbish Bin',  icon: Trash2 },
];

export default function AppShell() {
  const { user, logout, isManagerOrAbove } = useAuth();
  const location = useLocation();
  const [mobileOpen, setMobileOpen] = useState(false);
  const [userMenuOpen, setUserMenuOpen] = useState(false);
  const userMenuRef = useRef<HTMLDivElement>(null);

  // Close user menu on outside click
  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (userMenuRef.current && !userMenuRef.current.contains(e.target as Node)) {
        setUserMenuOpen(false);
      }
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, []);

  const isActive = (to: string) => {
    if (to === '/documents') {
      return location.pathname.startsWith('/documents') &&
             !location.pathname.startsWith('/documents/upload');
    }
    return location.pathname.startsWith(to);
  };

  const initials = (user?.displayName ?? user?.username ?? '?')
    .split(' ')
    .map((p: string) => p[0])
    .join('')
    .slice(0, 2)
    .toUpperCase();

  const SidebarLink = ({ to, label, icon: Icon }: { to: string; label: string; icon: React.ElementType }) => (
    <Link
      to={to}
      onClick={() => setMobileOpen(false)}
      className={`flex items-center gap-3 px-3 py-2.5 rounded-md text-sm transition-colors ${
        isActive(to)
          ? 'bg-primary/10 text-primary font-medium'
          : 'text-muted-foreground hover:bg-muted hover:text-foreground'
      }`}
    >
      <Icon className="h-5 w-5 flex-shrink-0" />
      {label}
    </Link>
  );

  // Full desktop sidebar content
  const DesktopSidebarContent = () => (
    <div className="flex flex-col h-full">
      <div className="p-4 border-b border-border">
        <Link to="/dashboard" className="flex items-center gap-2">
          <div className="w-8 h-8 rounded-lg bg-primary flex items-center justify-center flex-shrink-0">
            <Cpu className="h-4 w-4 text-primary-foreground" />
          </div>
          <span className="font-semibold text-lg text-foreground">OCR ERP</span>
        </Link>
      </div>

      <nav className="flex-1 p-3 space-y-1 overflow-y-auto">
        {navItems.map(item => <SidebarLink key={item.to} {...item} />)}
        {isManagerOrAbove && (
          <>
            <div className="pt-4 pb-1 px-3 text-xs font-semibold text-muted-foreground uppercase tracking-wider">
              Admin
            </div>
            {adminItems.map(item => <SidebarLink key={item.to} {...item} />)}
          </>
        )}
      </nav>

      <div className="p-3 border-t border-border space-y-1">
        <div className="flex items-center gap-3 px-3 py-2">
          <div className="w-8 h-8 rounded-full bg-muted flex items-center justify-center text-xs font-medium text-muted-foreground flex-shrink-0">
            {initials}
          </div>
          <div className="flex-1 min-w-0">
            <p className="text-sm font-medium text-foreground truncate">
              {user?.displayName ?? user?.username}
            </p>
            <p className="text-xs text-muted-foreground capitalize">{user?.role?.toLowerCase()}</p>
          </div>
        </div>
        <button
          onClick={() => logout()}
          className="flex items-center gap-3 px-3 py-2 rounded-md text-sm text-muted-foreground hover:bg-muted hover:text-foreground w-full transition-colors"
        >
          <LogOut className="h-5 w-5" />
          Sign out
        </button>
      </div>
    </div>
  );

  return (
    <div className="min-h-screen flex bg-background">

      {/* ── Desktop sidebar ─────────────────────────────────────────────── */}
      <aside className="hidden md:flex flex-col w-64 bg-card border-r border-border sticky top-0 h-screen flex-shrink-0">
        <DesktopSidebarContent />
      </aside>

      {/* ── Mobile "More" drawer — admin items only ──────────────────────── */}
      {mobileOpen && (
        <div className="fixed inset-0 z-50 md:hidden">
          <div
            className="fixed inset-0 bg-black/50 backdrop-blur-sm"
            onClick={() => setMobileOpen(false)}
          />
          <aside className="fixed left-0 top-0 bottom-0 w-72 bg-card border-r border-border z-50 flex flex-col shadow-xl">
            {/* Drawer header */}
            <div className="flex items-center justify-between px-4 py-3 border-b border-border">
              <Link to="/dashboard" onClick={() => setMobileOpen(false)} className="flex items-center gap-2">
                <div className="w-8 h-8 rounded-lg bg-primary flex items-center justify-center flex-shrink-0">
                  <Cpu className="h-4 w-4 text-primary-foreground" />
                </div>
                <span className="font-semibold text-lg text-foreground">OCR ERP</span>
              </Link>
              <button
                onClick={() => setMobileOpen(false)}
                className="p-2 rounded-md hover:bg-muted text-muted-foreground"
              >
                <X className="h-5 w-5" />
              </button>
            </div>

            {/* Admin nav items only */}
            <nav className="flex-1 p-3 space-y-1 overflow-y-auto">
              {isManagerOrAbove ? (
                <>
                  <div className="pb-1 px-3 text-xs font-semibold text-muted-foreground uppercase tracking-wider">
                    Admin
                  </div>
                  {adminItems.map(item => <SidebarLink key={item.to} {...item} />)}
                </>
              ) : (
                <p className="text-sm text-muted-foreground px-3 py-4">No additional options.</p>
              )}
            </nav>
          </aside>
        </div>
      )}

      {/* ── Main content area ────────────────────────────────────────────── */}
      <div className="flex-1 flex flex-col min-h-screen overflow-hidden">

        {/* Mobile top bar */}
        <header className="md:hidden sticky top-0 z-30 border-b border-border bg-card/95 backdrop-blur-sm px-4 h-14 flex items-center justify-between">
          <Link to="/dashboard" className="flex items-center gap-2">
            <div className="w-7 h-7 rounded-lg bg-primary flex items-center justify-center flex-shrink-0">
              <Cpu className="h-3.5 w-3.5 text-primary-foreground" />
            </div>
            <span className="font-semibold text-foreground">OCR ERP</span>
          </Link>

          {/* User avatar — opens user dropdown */}
          <div className="relative" ref={userMenuRef}>
            <button
              onClick={() => setUserMenuOpen(o => !o)}
              className="w-8 h-8 rounded-full bg-primary/10 flex items-center justify-center text-xs font-semibold text-primary"
            >
              {initials}
            </button>

            {/* User dropdown panel */}
            {userMenuOpen && (
              <div className="absolute right-0 top-full mt-2 w-52 bg-card border border-border rounded-xl shadow-lg overflow-hidden z-50">
                {/* User info */}
                <div className="px-4 py-3 border-b border-border">
                  <p className="text-sm font-medium text-foreground truncate">
                    {user?.displayName ?? user?.username}
                  </p>
                  <p className="text-xs text-muted-foreground capitalize">{user?.role?.toLowerCase()}</p>
                </div>
                {/* Sign out */}
                <button
                  onClick={() => { setUserMenuOpen(false); logout(); }}
                  className="flex items-center gap-2.5 w-full px-4 py-3 text-sm text-destructive hover:bg-red-50 transition-colors"
                >
                  <LogOut className="h-4 w-4" />
                  Sign out
                </button>
              </div>
            )}
          </div>
        </header>

        {/* Page content */}
        <main className="flex-1 overflow-auto p-4 md:p-6 pb-24 md:pb-6">
          <Outlet />
        </main>

        {/* ── Mobile bottom navigation bar ─────────────────────────────── */}
        <nav
          className="md:hidden fixed bottom-0 left-0 right-0 z-40 bg-card/95 backdrop-blur-sm border-t border-border flex items-stretch"
          style={{ paddingBottom: 'env(safe-area-inset-bottom)' }}
        >
          {/* Dashboard / Documents / Upload */}
          {navItems.map(({ to, label, icon: Icon }) => {
            const active = isActive(to);
            return (
              <Link
                key={to}
                to={to}
                className={`flex-1 flex flex-col items-center justify-center py-2 gap-0.5 min-h-[56px] relative transition-colors ${
                  active ? 'text-primary' : 'text-muted-foreground active:bg-muted'
                }`}
              >
                <Icon className="h-5 w-5" />
                <span className="text-[10px] font-medium leading-none">{label}</span>
                {active && (
                  <span className="absolute bottom-0 left-0 right-0 h-0.5 bg-primary rounded-t-full" />
                )}
              </Link>
            );
          })}

          {/* More — opens admin drawer (only shown if manager+, hidden otherwise) */}
          {isManagerOrAbove && (
            <button
              onClick={() => setMobileOpen(o => !o)}
              className={`flex-1 flex flex-col items-center justify-center py-2 gap-0.5 min-h-[56px] transition-colors relative ${
                mobileOpen ? 'text-primary' : 'text-muted-foreground active:bg-muted'
              }`}
            >
              <MoreHorizontal className="h-5 w-5" />
              <span className="text-[10px] font-medium leading-none">More</span>
              {mobileOpen && (
                <span className="absolute bottom-0 left-0 right-0 h-0.5 bg-primary rounded-t-full" />
              )}
            </button>
          )}
        </nav>
      </div>
    </div>
  );
}
