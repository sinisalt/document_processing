import { useTranslation } from "react-i18next";
import { useTheme } from "next-themes";
import { Languages, LogOut, Monitor, Moon, Sun } from "lucide-react";
import {
  Avatar,
  AvatarFallback,
  Badge,
  Button,
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuRadioGroup,
  DropdownMenuRadioItem,
  DropdownMenuSeparator,
  DropdownMenuSub,
  DropdownMenuSubContent,
  DropdownMenuSubTrigger,
  DropdownMenuTrigger,
} from "@docproc/ui";
import { useAuth } from "../auth/useAuth";
import { initials } from "../lib/format";
import { currentLanguage, supportedLanguages } from "../i18n";

/** Avatar in the header: who is signed in, their roles and scope, language, appearance, sign out. */
export function UserMenu({ placement = "header" }: { placement?: "header" | "sidebar" }) {
  const { t, i18n } = useTranslation("common");
  const { user, signOut } = useAuth();
  const { theme = "system", setTheme } = useTheme();
  if (!user) return null;

  const language = currentLanguage();
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button
          variant="ghost"
          className={placement === "sidebar" ? "h-10 w-full justify-start gap-2 px-2" : "h-9 gap-2 px-2"}
          aria-label={t("userMenu.open", { name: user.displayName })}
          data-testid={placement === "sidebar" ? "sidebar-user-menu" : "user-menu"}
        >
          <Avatar size="sm">
            <AvatarFallback>{initials(user.displayName)}</AvatarFallback>
          </Avatar>
          <span className={placement === "sidebar" ? "truncate text-sm font-medium" : "hidden max-w-40 truncate text-sm font-medium sm:inline"}>{user.displayName}</span>
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align={placement === "sidebar" ? "start" : "end"} side={placement === "sidebar" ? "top" : "bottom"} className="min-w-64">
        <DropdownMenuLabel className="flex flex-col gap-1 font-normal">
          <span className="font-medium">{user.displayName}</span>
          {user.email && <span className="text-xs text-muted-foreground">{user.email}</span>}
          <span className="text-xs text-muted-foreground">{t(`scope.${user.scope}`)}</span>
        </DropdownMenuLabel>
        <div className="flex flex-wrap gap-1 px-2 pb-2" aria-label={t("userMenu.roles")}>
          {user.roles.map((role) => (
            <Badge key={role} variant="secondary">
              {t(`roles.${role}`, { defaultValue: role })}
            </Badge>
          ))}
        </div>
        <DropdownMenuSeparator />
        <DropdownMenuSub>
          <DropdownMenuSubTrigger>
            <Languages aria-hidden />
            {t("userMenu.language")}
            <span className="ml-auto text-xs text-muted-foreground uppercase">{language}</span>
          </DropdownMenuSubTrigger>
          <DropdownMenuSubContent>
            <DropdownMenuRadioGroup value={language} onValueChange={(value) => void i18n.changeLanguage(value)}>
              {supportedLanguages.map((code) => (
                <DropdownMenuRadioItem key={code} value={code} lang={code}>
                  {t(`languages.${code}`)}
                </DropdownMenuRadioItem>
              ))}
            </DropdownMenuRadioGroup>
          </DropdownMenuSubContent>
        </DropdownMenuSub>
        <DropdownMenuSub>
          <DropdownMenuSubTrigger>
            {theme === "dark" ? <Moon aria-hidden /> : theme === "light" ? <Sun aria-hidden /> : <Monitor aria-hidden />}
            {t("userMenu.theme")}
          </DropdownMenuSubTrigger>
          <DropdownMenuSubContent>
            <DropdownMenuRadioGroup value={theme} onValueChange={setTheme}>
              <DropdownMenuRadioItem value="light">
                <Sun aria-hidden />
                {t("userMenu.themeLight")}
              </DropdownMenuRadioItem>
              <DropdownMenuRadioItem value="dark">
                <Moon aria-hidden />
                {t("userMenu.themeDark")}
              </DropdownMenuRadioItem>
              <DropdownMenuRadioItem value="system">
                <Monitor aria-hidden />
                {t("userMenu.themeSystem")}
              </DropdownMenuRadioItem>
            </DropdownMenuRadioGroup>
          </DropdownMenuSubContent>
        </DropdownMenuSub>
        <DropdownMenuSeparator />
        <DropdownMenuItem onSelect={() => void signOut()} variant="destructive">
          <LogOut aria-hidden />
          {t("auth.signOut")}
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
