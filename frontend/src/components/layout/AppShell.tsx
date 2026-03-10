import { Link, useLocation, Outlet } from 'react-router-dom';
import { useAuth } from '../../contexts/AuthContext';
import {
  LayoutDashboard, FileText, Upload, Settings, Users,
  ClipboardList, LogOut, Menu, X, Cpu, FlaskConical, Building2, Trash2,
} from 'lucide-react';
import { useState } from 'react';

const navItems = [
  { to: '/dashboard',          label: 'Dashboard',    icon: LayoutDashboard },
  { to: '/documents',          label: 'Documents',    icon: FileText },
  { to: '/documents/upload',   label: 'Upload',       icon: Upload },
];

const adminItems = [
  { to: '/admin/vendors',      label: 'Vendors',      icon: Building2 },
  { to: '/admin/users',        label: 'Users',        icon: Users },
  { to: '/admin/config',       label: 'Field Config', icon: Settings },
  { to: '/admin/audit',        label: 'Audit Log',    icon: ClipboardList },
  { to: '/admin/erp-test',     label: 'ERP Test',     icon: FlaskConical },
  { to: '/admin/rubbish-bin',  label: 'Rubbish Bin',  icon: Trash2 },
];

export default function AppShell() {
  const { user, logout, isManagerOrAbove } = useAuth();
  const location = useLocation();
  const [mobileOpen, setMobileOpen] = useState(false);

  const isActive = (to: string) => location.pathname.startsWith(to);

  const NavLink = ({ to, label, icon: Icon }: { to: string; label: string; icon: React.ElementType }) => (
    <Link
      to={to}
      onClick={() => setMobileOpen(false)}
      className={`flex items-center gap-3 px-3 py-2 rounded-md text-sm transition-colors ${
        isActive(to)
          ? 'bg-primary/10 text-primary font-medium'
          : 'text-muted-foreground hover:bg-muted hover:text-foreground'
      }`}
    >
      <Icon className="h-5 w-5 flex-shrink-0" />
      {label}
    </Link>
  );

  const initials = (user?.displayName ?? user?.username ?? '?')
    .split(' ')
    .map((p: string) => p[0])
    .join('')
    .slice(0, 2)
    .toUpperCase();

  const Sidebar = () => (
    <div className="flex flex-col h-full">
      {/* Logo */}
      <div className="p-4 border-b border-border">
        <Link to="/dashboard" className="flex items-center gap-2">
          <div className="w-8 h-8 rounded-lg bg-primary flex items-center justify-center flex-shrink-0">
            <Cpu className="h-4 w-4 text-primary-foreground" />
          </div>
          <span className="font-semibold text-lg text-foreground">OCR ERP</span>
        </Link>
      </div>

      {/* Navigation */}
      <nav className="flex-1 p-3 space-y-1 overflow-y-auto">
        {navItems.map(item => <NavLink key={item.to} {...item} />)}

        {isManagerOrAbove && (
          <>
            <div className="pt-4 pb-1 px-3 text-xs font-semibold text-muted-foreground uppercase tracking-wider">
              Admin
            </div>
            {adminItems.map(item => <NavLink key={item.to} {...item} />)}
          </>
        )}
      </nav>

      {/* User + Sign-out */}
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
      {/* Desktop sidebar — sticky so sign-out stays visible regardless of content height */}
      <aside className="hidden md:flex flex-col w-64 bg-card border-r border-border sticky top-0 h-screen flex-shrink-0">
        <Sidebar />
      </aside>

      {/* Mobile overlay */}
      {mobileOpen && (
        <div className="fixed inset-0 z-40 md:hidden">
          <div className="fixed inset-0 bg-black/50" onClick={() => setMobileOpen(false)} />
          <aside className="fixed left-0 top-0 bottom-0 w-64 bg-card border-r border-border z-50">
            <div className="flex justify-end p-4">
              <button onClick={() => setMobileOpen(false)}>
                <X className="h-6 w-6 text-muted-foreground" />
              </button>
            </div>
            <Sidebar />
          </aside>
        </div>
      )}

      {/* Main content */}
      <div className="flex-1 flex flex-col min-h-screen overflow-hidden">
        {/* Mobile top bar */}
        <header className="md:hidden border-b border-border bg-card px-4 py-3 flex items-center gap-3">
          <button onClick={() => setMobileOpen(true)} className="p-1 rounded-md hover:bg-muted">
            <Menu className="h-6 w-6 text-muted-foreground" />
          </button>
          <span className="font-semibold text-foreground">OCR ERP</span>
        </header>

        <main className="flex-1 overflow-auto p-4 md:p-6">
          <Outlet />
        </main>
      </div>
    </div>
  );
}
