import { Link, useLocation, Outlet } from 'react-router-dom';
import { useAuth } from '../../contexts/AuthContext';
import {
  LayoutDashboard, FileText, Upload, Settings, Users,
  ClipboardList, LogOut, Menu, X
} from 'lucide-react';
import { useState } from 'react';

const navItems = [
  { to: '/dashboard', label: 'Dashboard', icon: LayoutDashboard },
  { to: '/documents', label: 'Documents', icon: FileText },
  { to: '/documents/upload', label: 'Upload', icon: Upload },
];

const adminItems = [
  { to: '/admin/users', label: 'Users', icon: Users },
  { to: '/admin/config', label: 'Field Config', icon: Settings },
  { to: '/admin/audit', label: 'Audit Log', icon: ClipboardList },
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
      className={`flex items-center gap-3 px-3 py-2 rounded-lg text-sm font-medium transition-colors ${
        isActive(to)
          ? 'bg-blue-50 text-blue-700'
          : 'text-gray-600 hover:bg-gray-100 hover:text-gray-900'
      }`}
    >
      <Icon className="h-5 w-5 flex-shrink-0" />
      {label}
    </Link>
  );

  const Sidebar = () => (
    <nav className="flex flex-col h-full p-4 gap-1">
      <div className="mb-6 px-3">
        <h1 className="text-lg font-bold text-gray-900">OCR ERP</h1>
        <p className="text-xs text-gray-500">{user?.displayName}</p>
        <span className={`badge mt-1 ${
          user?.role === 'Admin' ? 'bg-purple-100 text-purple-700' :
          user?.role === 'Manager' ? 'bg-blue-100 text-blue-700' :
          'bg-gray-100 text-gray-600'
        }`}>{user?.role}</span>
      </div>

      {navItems.map((item) => <NavLink key={item.to} {...item} />)}

      {isManagerOrAbove && (
        <>
          <div className="mt-4 mb-2 px-3 text-xs font-semibold text-gray-400 uppercase tracking-wider">
            Admin
          </div>
          {adminItems.map((item) => <NavLink key={item.to} {...item} />)}
        </>
      )}

      <div className="mt-auto">
        <button
          onClick={logout}
          className="flex items-center gap-3 px-3 py-2 rounded-lg text-sm font-medium text-red-600 hover:bg-red-50 w-full transition-colors"
        >
          <LogOut className="h-5 w-5" />
          Sign out
        </button>
      </div>
    </nav>
  );

  return (
    <div className="flex h-screen bg-gray-50">
      {/* Desktop sidebar */}
      <aside className="hidden md:flex flex-col w-64 bg-white border-r border-gray-200">
        <Sidebar />
      </aside>

      {/* Mobile overlay */}
      {mobileOpen && (
        <div className="fixed inset-0 z-40 md:hidden">
          <div className="fixed inset-0 bg-black/50" onClick={() => setMobileOpen(false)} />
          <aside className="fixed left-0 top-0 bottom-0 w-64 bg-white z-50">
            <div className="flex justify-end p-4">
              <button onClick={() => setMobileOpen(false)}>
                <X className="h-6 w-6 text-gray-500" />
              </button>
            </div>
            <Sidebar />
          </aside>
        </div>
      )}

      {/* Main content */}
      <div className="flex-1 flex flex-col overflow-hidden">
        {/* Top bar (mobile) */}
        <header className="md:hidden flex items-center justify-between px-4 py-3 bg-white border-b border-gray-200">
          <button onClick={() => setMobileOpen(true)}>
            <Menu className="h-6 w-6 text-gray-600" />
          </button>
          <span className="font-semibold text-gray-900">OCR ERP</span>
          <div className="w-6" />
        </header>

        <main className="flex-1 overflow-auto p-4 md:p-6">
          <Outlet />
        </main>
      </div>
    </div>
  );
}
